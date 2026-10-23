using System.Net.Sockets;
using Builder.Core.Rcon;
using Builder.Storage.Collection;
using Microsoft.Extensions.Time.Testing;

namespace Builder.Storage.Tests;

public class StatusParserTests
{
    [Fact]
    public void ParsesTickQueryWhenLagging()
    {
        // RCON은 여러 메시지를 줄바꿈 없이 이어 붙일 수 있다.
        const string response = "The game is running, but can't keep up with the current tick rate" +
                                "Target tick rate: 20.0 per second.\nAverage time per tick: 62.5ms (Target: 50.0ms)" +
                                "Percentiles: P50: 55.1ms P95: 80.3ms P99: 120.0ms, sample: 100";
        var s = StatusParsers.ParseTickQuery(response)!;
        Assert.Equal(16, s.Tps);
        Assert.Equal(62.5, s.MsptAvg);
        Assert.Equal(80.3, s.MsptP95);
    }

    [Fact]
    public void TickQueryTpsIsCappedAtTargetRate()
    {
        var s = StatusParsers.ParseTickQuery(
            "The game is running normally\nTarget tick rate: 20.0 per second.\nAverage time per tick: 3.2ms (Target: 50.0ms)\n" +
            "Percentiles: P50: 3.0ms P95: 4.1ms P99: 9.9ms, sample: 100")!;
        Assert.Equal(20, s.Tps);
        Assert.Equal(3.2, s.MsptAvg);
    }

    [Fact]
    public void TickQueryAcceptsCommaDecimalsAndFrozenState()
    {
        var comma = StatusParsers.ParseTickQuery("Target tick rate: 20,0 per second.Average time per tick: 3,5ms (Target: 50,0ms)")!;
        Assert.Equal(3.5, comma.MsptAvg);
        Assert.Null(comma.MsptP95);

        var frozen = StatusParsers.ParseTickQuery("The game is frozen\nTarget tick rate: 20.0 per second.\nAverage time per tick: 0.4ms (Target: 50.0ms)")!;
        Assert.Equal(0, frozen.Tps);
    }

    [Fact]
    public void TickQueryReturnsNullForUnknownCommand()
    {
        const string unknown = "Unknown or incomplete command, see below for errortick query<--[HERE]";
        Assert.Null(StatusParsers.ParseTickQuery(unknown));
        Assert.True(StatusParsers.IsUnknownCommand(unknown));
    }

    [Fact]
    public void ParsesSparkTps()
    {
        const string response = """
            TPS from last 5s, 10s, 1m, 5m, 15m:
             *20.0, *20.0, 19.97, 19.99, 20.0

            Tick durations (min/med/95%ile/max ms) from last 10s, 1m:
             0.4/1.2/3.6/18.4;  0.3/1.1/2.9/45.0
            """;
        var s = StatusParsers.ParseSparkTps(response)!;
        Assert.Equal(20, s.Tps);
        Assert.Equal(1.2, s.MsptAvg);
        Assert.Equal(3.6, s.MsptP95);
        Assert.Null(StatusParsers.ParseSparkTps("Unknown or incomplete command"));
    }

    [Theory]
    [InlineData("There are 0 of a max of 8 players online: ", 0, 8, new string[0])]
    [InlineData("There are 2 of a max of 20 players online: Alex, Steve", 2, 20, new[] { "Alex", "Steve" })]
    [InlineData("There are 1 of a max 10 players online:\nNotch", 1, 10, new[] { "Notch" })]
    public void ParsesPlayerList(string response, int online, int max, string[] names)
    {
        var list = StatusParsers.ParsePlayerList(response)!;
        Assert.Equal(online, list.Online);
        Assert.Equal(max, list.Max);
        Assert.Equal(names, list.Names);
    }
}

public class MetricsCollectorTests : StoreTestBase
{
    private const string TickResponse =
        "The game is running normally\nTarget tick rate: 20.0 per second.\nAverage time per tick: 12.0ms (Target: 50.0ms)\n" +
        "Percentiles: P50: 11.0ms P95: 20.0ms P99: 30.0ms, sample: 100";

    private readonly FakeTimeProvider time = new(T0);
    private readonly List<string> logs = [];
    private readonly FakeSession session = new();
    private int connectAttempts;
    private Func<Exception?> connectFailure = () => null;

    private MetricsCollector Collector(MetricsWriter writer, ProcessSnapshot? process = null) =>
        new(writer, _ =>
        {
            connectAttempts++;
            var failure = connectFailure();
            return failure is null ? Task.FromResult<IRconSession>(session) : Task.FromException<IRconSession>(failure);
        }, () => process, logs.Add, time);

    private MetricsWriter Writer(MetricsStore store) => new(store, time: time);

    [Fact]
    public async Task RecordsTickPlayersAndProcessMetrics()
    {
        var store = OpenStore();
        session.Responses["tick query"] = TickResponse;
        session.Responses["list"] = "There are 1 of a max of 8 players online: Alex";

        await using (var writer = Writer(store))
            await Collector(writer, new ProcessSnapshot(3000, 25.5)).CollectOnceAsync(CancellationToken.None);

        var sample = Assert.Single(store.RecentSamples(10));
        Assert.Equal(new MetricSample(T0) { Tps = 20, MsptAvg = 12, MsptP95 = 20, RssMb = 3000, CpuPercent = 25.5, Players = 1 }, sample);
        Assert.Contains("RCON 연결됨 — TPS·접속자 수집 시작", logs);
    }

    [Fact]
    public async Task FallsBackToSparkOnceTickQueryIsUnsupported()
    {
        var store = OpenStore();
        session.Responses["spark tps"] = "TPS from last 5s, 10s, 1m, 5m, 15m:\n 18.5, 19.0, 19.5, 20.0, 20.0\n\n" +
                                         "Tick durations (min/med/95%ile/max ms) from last 10s, 1m:\n 1.0/40.0/70.0/90.0;  1.0/30.0/60.0/90.0";
        session.Responses["list"] = "There are 0 of a max of 8 players online: ";

        await using (var writer = Writer(store))
        {
            var collector = Collector(writer);
            await collector.CollectOnceAsync(CancellationToken.None);
            await collector.CollectOnceAsync(CancellationToken.None);
        }

        Assert.Equal(1, session.Commands.Count(c => c == "tick query"));
        Assert.Equal(2, session.Commands.Count(c => c == "spark tps"));
        Assert.All(store.RecentSamples(10), s => Assert.Equal(18.5, s.Tps));
    }

    [Fact]
    public async Task WaitsForRconWhileServerStartsAndKeepsProcessMetrics()
    {
        var store = OpenStore();
        connectFailure = () => new SocketException((int)SocketError.ConnectionRefused);
        session.Responses["tick query"] = TickResponse;
        session.Responses["list"] = "There are 0 of a max of 8 players online: ";

        await using (var writer = Writer(store))
        {
            var collector = Collector(writer, new ProcessSnapshot(1000, null));
            await collector.CollectOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(30));
            await collector.CollectOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(30));
            connectFailure = () => null;
            await collector.CollectOnceAsync(CancellationToken.None);
        }

        Assert.Equal(3, connectAttempts);
        Assert.Single(logs, l => l.StartsWith("RCON 대기 중"));
        var samples = store.RecentSamples(10);
        Assert.Equal([null, null, 20.0], samples.Select(s => s.Tps));
        Assert.All(samples, s => Assert.Equal(1000, s.RssMb));
    }

    [Fact]
    public async Task StopsRetryingAfterWrongPassword()
    {
        var store = OpenStore();
        connectFailure = () => new RconAuthenticationException("RCON 비밀번호가 맞지 않습니다.");
        await using (var writer = Writer(store))
        {
            var collector = Collector(writer, new ProcessSnapshot(1000, null));
            await collector.CollectOnceAsync(CancellationToken.None);
            await collector.CollectOnceAsync(CancellationToken.None);
        }
        Assert.Equal(1, connectAttempts);
        Assert.Equal(2, store.RecentSamples(10).Count);
    }

    [Fact]
    public async Task ReconnectsAfterConnectionLoss()
    {
        var store = OpenStore();
        session.Responses["tick query"] = TickResponse;
        session.Responses["list"] = "There are 0 of a max of 8 players online: ";
        await using (var writer = Writer(store))
        {
            var collector = Collector(writer);
            session.Failure = new IOException("connection reset");
            await collector.CollectOnceAsync(CancellationToken.None);
            Assert.Equal(1, session.DisposeCount);

            session.Failure = null;
            await collector.CollectOnceAsync(CancellationToken.None);
        }
        Assert.Equal(2, connectAttempts);
        Assert.Contains(logs, l => l.StartsWith("RCON 연결이 끊겼습니다"));
        Assert.Equal(20, Assert.Single(store.RecentSamples(10)).Tps);
    }

    [Fact]
    public async Task RecordsJoinAndLeaveFromPlayerListChanges()
    {
        var store = OpenStore();
        session.Responses["tick query"] = TickResponse;
        await using (var writer = Writer(store))
        {
            var collector = Collector(writer);
            session.Responses["list"] = "There are 1 of a max of 8 players online: Alex";
            await collector.CollectOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(30));
            session.Responses["list"] = "There are 2 of a max of 8 players online: Alex, Steve";
            await collector.CollectOnceAsync(CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(30));
            session.Responses["list"] = "There are 1 of a max of 8 players online: Steve";
            await collector.CollectOnceAsync(CancellationToken.None);
        }

        // 첫 측정은 기준점이라 이벤트를 남기지 않는다 (수집 시작 전부터 있던 접속자).
        var events = store.Events(T0, T0.AddHours(1));
        Assert.Equal(
            [(EventTypes.PlayerJoin, """{"player":"Steve"}""", T0.AddSeconds(30)), (EventTypes.PlayerLeave, """{"player":"Alex"}""", T0.AddSeconds(60))],
            events.Select(e => (e.Type, e.Payload, e.Timestamp)));
    }

    [Fact]
    public async Task WithoutRconRecordsProcessMetricsOnly()
    {
        var store = OpenStore();
        await using (var writer = Writer(store))
            await new MetricsCollector(writer, null, () => new ProcessSnapshot(500, 1), logs.Add, time).CollectOnceAsync(CancellationToken.None);
        var sample = Assert.Single(store.RecentSamples(10));
        Assert.Equal(500, sample.RssMb);
        Assert.Null(sample.Tps);
    }

    private sealed class FakeSession : IRconSession
    {
        public Dictionary<string, string> Responses { get; } = [];
        public List<string> Commands { get; } = [];
        public Exception? Failure { get; set; }
        public int DisposeCount { get; private set; }

        public Task<string> ExecuteAsync(string command, CancellationToken ct)
        {
            Commands.Add(command);
            if (Failure is not null)
                return Task.FromException<string>(Failure);
            return Task.FromResult(Responses.GetValueOrDefault(command, "Unknown or incomplete command, see below for error"));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
