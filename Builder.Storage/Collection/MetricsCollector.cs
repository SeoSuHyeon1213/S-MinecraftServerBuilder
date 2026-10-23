using System.Diagnostics;
using System.Text.Json;
using Builder.Core.Rcon;
using Builder.Core.Server;

namespace Builder.Storage.Collection;

/// <summary>RCON 명령 실행 창구. 테스트에서 가짜로 바꿀 수 있게 분리했다.</summary>
public interface IRconSession : IAsyncDisposable
{
    Task<string> ExecuteAsync(string command, CancellationToken ct);
}

/// <summary>Java 프로세스 메모리·CPU 측정값.</summary>
public sealed record ProcessSnapshot(long RssMb, double? CpuPercent);

/// <summary>실행 중인 프로세스의 상주 메모리와, 직전 측정 이후 CPU 사용률(전체 코어 대비 %)을 잰다.</summary>
public sealed class ProcessProbe(Process process, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private TimeSpan? lastCpu;
    private long lastTimestamp;

    public ProcessSnapshot? Measure()
    {
        try
        {
            process.Refresh();
            if (process.HasExited)
                return null;

            var cpu = process.TotalProcessorTime;
            var now = time.GetTimestamp();
            double? percent = null;
            if (lastCpu is { } previous)
            {
                var wall = time.GetElapsedTime(lastTimestamp, now);
                if (wall > TimeSpan.Zero)
                    percent = Math.Round((cpu - previous) / wall / Environment.ProcessorCount * 100, 1);
            }
            lastCpu = cpu;
            lastTimestamp = now;
            return new ProcessSnapshot(process.WorkingSet64 / (1024 * 1024), percent);
        }
        catch (InvalidOperationException)
        {
            // 측정 도중 프로세스가 끝난 경우
            return null;
        }
    }
}

/// <summary>
/// 서버 실행 중 주기적으로 지표를 모아 <see cref="MetricsWriter"/>로 보낸다.
/// TPS·MSPT는 바닐라 <c>/tick query</c>, 없으면 spark <c>/spark tps</c>를 쓰고, 접속자는 <c>/list</c>로 얻는다.
/// RCON이 아직 열리지 않았거나(서버 시작 중) 끊겨도 프로세스 지표는 계속 기록하고 다음 주기에 다시 접속한다.
/// </summary>
public sealed class MetricsCollector
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private readonly MetricsWriter writer;
    private readonly Func<CancellationToken, Task<IRconSession>>? connect;
    private readonly Func<ProcessSnapshot?> probe;
    private readonly TimeProvider time;
    private readonly Action<string> log;

    private IRconSession? session;
    private bool rconDisabled;
    private bool waitingLogged;
    private bool tickQueryUnsupported;
    private bool sparkUnsupported;
    private HashSet<string>? lastPlayers;

    /// <param name="connect">null이면 RCON 없이 프로세스 지표만 모은다.</param>
    public MetricsCollector(MetricsWriter writer, Func<CancellationToken, Task<IRconSession>>? connect,
        Func<ProcessSnapshot?> probe, Action<string> log, TimeProvider? time = null)
    {
        this.writer = writer;
        this.connect = connect;
        this.probe = probe;
        this.log = log;
        this.time = time ?? TimeProvider.System;
        rconDisabled = connect is null;
    }

    /// <summary>
    /// <see cref="ServerService.RunAsync"/>의 모니터로 쓴다. 서버 폴더의 지표 DB를 열고, 서버가 끝날 때까지 수집한다.
    /// </summary>
    public static async Task MonitorAsync(ServerRunContext context, Action<string> log, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(context.Preset.MetricsIntervalSeconds);
        var store = MetricsStore.OpenForServer(context.Directory);
        var options = new MetricsWriterOptions
        {
            // 짧은 주기에서도 DB 커밋은 5초에 한 번 이하로 한다.
            FlushInterval = TimeSpan.FromSeconds(Math.Clamp(context.Preset.MetricsIntervalSeconds, 5, 60)),
        };

        var endpoint = RconSettings.Read(context.Directory);
        if (endpoint is null)
            log("주의: server.properties에서 RCON이 꺼져 있어 TPS·접속자 없이 프로세스 메모리·CPU만 기록합니다.");
        Func<CancellationToken, Task<IRconSession>>? connect = endpoint is null
            ? null
            : async token => new RconSession(await RconClient.ConnectAsync(endpoint, CommandTimeout, token));

        log($"지표 수집: {interval.TotalSeconds:0}초 간격 → {store.DatabasePath}");
        await using var writer = new MetricsWriter(store, options, log: log);
        var collector = new MetricsCollector(writer, connect, new ProcessProbe(context.Process).Measure, log);

        await writer.AddAsync(new ServerEvent(DateTimeOffset.UtcNow, EventTypes.ServerStart, JsonSerializer.Serialize(new
        {
            minecraft = context.Config.MinecraftVersion,
            loader = context.Config.Loader,
            loaderVersion = context.Config.LoaderVersion,
            preset = context.Config.Preset,
            heapMb = context.Preset.HeapMb,
        })), CancellationToken.None);

        try
        {
            await collector.RunAsync(interval, ct);
        }
        finally
        {
            await collector.CloseAsync();
            int? exitCode = context.Process.HasExited ? context.Process.ExitCode : null;
            await writer.AddAsync(new ServerEvent(DateTimeOffset.UtcNow, EventTypes.ServerStop,
                JsonSerializer.Serialize(new { exitCode })), CancellationToken.None);
        }
    }

    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            do
            {
                await CollectOnceAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>한 번 측정해 기록 큐에 넣는다.</summary>
    internal async Task CollectOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var process = probe();
        TickStatus? tick = null;
        PlayerList? players = null;

        var rcon = await GetSessionAsync(ct);
        if (rcon is not null)
        {
            try
            {
                tick = await QueryTickAsync(rcon, ct);
                var listResponse = await rcon.ExecuteAsync("list", ct);
                players = StatusParsers.ParsePlayerList(listResponse);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException
                                           or OperationCanceledException && !ct.IsCancellationRequested)
            {
                log($"RCON 연결이 끊겼습니다. 다음 주기에 다시 접속합니다: {ex.Message}");
                await CloseAsync();
            }
        }

        if (players is not null)
            await RecordPlayerChangesAsync(now, players, ct);

        if (process is null && tick is null && players is null)
            return;
        writer.Add(new MetricSample(now)
        {
            Tps = tick?.Tps,
            MsptAvg = tick?.MsptAvg,
            MsptP95 = tick?.MsptP95,
            RssMb = process?.RssMb,
            CpuPercent = process?.CpuPercent,
            Players = players?.Online,
        });
    }

    private async Task<TickStatus?> QueryTickAsync(IRconSession rcon, CancellationToken ct)
    {
        if (!tickQueryUnsupported)
        {
            var response = await rcon.ExecuteAsync("tick query", ct);
            var status = StatusParsers.ParseTickQuery(response);
            if (status is not null)
                return status;
            tickQueryUnsupported = true;
            log($"'/tick query' 응답을 해석하지 못해 spark로 시도합니다: {Shorten(response)}");
        }
        if (!sparkUnsupported)
        {
            var response = await rcon.ExecuteAsync("spark tps", ct);
            var status = StatusParsers.ParseSparkTps(response);
            if (status is not null)
                return status;
            sparkUnsupported = true;
            log($"'/spark tps' 응답도 해석하지 못해 TPS·MSPT는 기록하지 않습니다: {Shorten(response)}");
        }
        return null;
    }

    /// <summary>직전 목록과 비교해 접속·퇴장 이벤트를 남긴다. 수집 주기보다 짧게 머문 접속은 잡히지 않는다.</summary>
    private async Task RecordPlayerChangesAsync(DateTimeOffset now, PlayerList players, CancellationToken ct)
    {
        var current = players.Names.ToHashSet(StringComparer.Ordinal);
        if (lastPlayers is not null)
        {
            foreach (var name in current.Except(lastPlayers).Order(StringComparer.Ordinal))
                await writer.AddAsync(new ServerEvent(now, EventTypes.PlayerJoin, JsonSerializer.Serialize(new { player = name })), ct);
            foreach (var name in lastPlayers.Except(current).Order(StringComparer.Ordinal))
                await writer.AddAsync(new ServerEvent(now, EventTypes.PlayerLeave, JsonSerializer.Serialize(new { player = name })), ct);
        }
        lastPlayers = current;
    }

    private async Task<IRconSession?> GetSessionAsync(CancellationToken ct)
    {
        if (session is not null || rconDisabled)
            return session;
        try
        {
            session = await connect!(ct);
            waitingLogged = false;
            log("RCON 연결됨 — TPS·접속자 수집 시작");
        }
        catch (RconAuthenticationException ex)
        {
            // 서버는 시작할 때만 server.properties를 읽으므로 이번 실행에서는 다시 시도해도 소용없다.
            rconDisabled = true;
            log($"주의: {ex.Message} 이번 실행에서는 프로세스 지표만 기록합니다.");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                       or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // 서버가 월드를 불러오는 동안에는 RCON이 아직 열리지 않는다.
            if (!waitingLogged)
                log("RCON 대기 중 (서버 시작이 끝나면 자동으로 연결합니다)");
            waitingLogged = true;
        }
        return session;
    }

    internal async Task CloseAsync()
    {
        if (session is null)
            return;
        await session.DisposeAsync();
        session = null;
    }

    private static string Shorten(string text)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
    }

    private sealed class RconSession(RconClient client) : IRconSession
    {
        public Task<string> ExecuteAsync(string command, CancellationToken ct) => client.ExecuteAsync(command, CommandTimeout, ct);

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}

/// <summary>events 테이블의 type 값.</summary>
public static class EventTypes
{
    public const string ServerStart = "server-start";
    public const string ServerStop = "server-stop";
    public const string PlayerJoin = "player-join";
    public const string PlayerLeave = "player-leave";
}
