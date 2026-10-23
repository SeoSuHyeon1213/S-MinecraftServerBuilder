using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Builder.Core.Loaders;
using Builder.Core.Presets;
using Builder.Core.Rcon;
using Builder.Core.Server;

namespace Builder.Core.Tests;

public class RconSettingsTests
{
    [Fact]
    public void EnablesRconWithGeneratedPasswordAndQuietBroadcast()
    {
        var props = new ServerPropertiesFile(["motd=hi"]);
        Assert.True(RconSettings.EnsureEnabled(props));

        Assert.Equal("true", props.Get(RconSettings.EnableKey));
        Assert.Equal("25575", props.Get(RconSettings.PortKey));
        Assert.Equal("false", props.Get(RconSettings.BroadcastKey));
        var password = props.Get(RconSettings.PasswordKey)!;
        Assert.Equal(24, password.Length);
        Assert.All(password, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.Equal("hi", props.Get("motd"));

        // 두 번째 호출은 아무것도 바꾸지 않는다 (비밀번호를 다시 만들지 않음).
        Assert.False(RconSettings.EnsureEnabled(props));
        Assert.Equal(password, props.Get(RconSettings.PasswordKey));
    }

    [Fact]
    public void KeepsUserPortAndPassword()
    {
        var props = new ServerPropertiesFile(["enable-rcon=false", "rcon.port=30000", "rcon.password=mine"]);
        RconSettings.EnsureEnabled(props);
        Assert.Equal("true", props.Get(RconSettings.EnableKey));
        Assert.Equal("30000", props.Get(RconSettings.PortKey));
        Assert.Equal("mine", props.Get(RconSettings.PasswordKey));
    }

    [Fact]
    public void DefaultPortFollowsGamePort()
    {
        var props = new ServerPropertiesFile(["server-port=25600"]);
        RconSettings.EnsureEnabled(props);
        Assert.Equal("25610", props.Get(RconSettings.PortKey));
    }

    [Fact]
    public void ReadsEndpointOnlyWhenEnabledWithPassword()
    {
        Assert.Null(RconSettings.Read(new ServerPropertiesFile(["enable-rcon=false", "rcon.password=x"])));
        Assert.Null(RconSettings.Read(new ServerPropertiesFile(["enable-rcon=true", "rcon.password="])));

        Assert.Equal(new RconEndpoint("127.0.0.1", 25575, "x"),
            RconSettings.Read(new ServerPropertiesFile(["enable-rcon=true", "rcon.password=x", "server-ip="])));
        Assert.Equal(new RconEndpoint("192.168.0.5", 26000, "x"),
            RconSettings.Read(new ServerPropertiesFile(["enable-rcon=true", "rcon.password=x", "rcon.port=26000", "server-ip=192.168.0.5"])));
    }
}

public class MetricsPresetTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mchb-rcon-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static PresetCatalog Catalog() =>
        PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));

    // 설계서 4장 "백그라운드 작업" 행: 일반 실시간(1초), 중간 5초, 저 30초, 최저 최소화(60초)
    [Theory]
    [InlineData("normal", 1)]
    [InlineData("medium", 5)]
    [InlineData("low", 30)]
    [InlineData("minimum", 60)]
    public void PresetIntervalsMatchDesign(string id, int seconds) =>
        Assert.Equal(seconds, PresetResolver.Resolve(Catalog().Get(id), null).MetricsIntervalSeconds);

    [Fact]
    public void IntervalCanBeOverriddenOrDisabled()
    {
        var low = Catalog().Get("low");
        Assert.Equal(10, PresetResolver.Resolve(low, new Dictionary<string, string> { [PresetResolver.MetricsIntervalKey] = "10" }).MetricsIntervalSeconds);
        Assert.False(PresetResolver.Resolve(low, new Dictionary<string, string> { [PresetResolver.MetricsIntervalKey] = "0" }).MetricsEnabled);
        Assert.Throws<ArgumentException>(() =>
            PresetResolver.Resolve(low, new Dictionary<string, string> { [PresetResolver.MetricsIntervalKey] = "-1" }));
        Assert.Throws<ArgumentException>(() =>
            PresetResolver.Resolve(low, new Dictionary<string, string> { [PresetResolver.MetricsIntervalKey] = "fast" }));
        PresetResolver.ValidateKey(PresetResolver.MetricsIntervalKey);
    }

    [Fact]
    public void ApplyEnablesRconOnlyWhenMetricsEnabled()
    {
        var loader = new FabricServerLoader(new HttpClient());
        var low = Catalog().Get("low");
        var propertiesPath = Path.Combine(dir, ServerPropertiesFile.FileName);

        ServerLaunch.Apply(dir, PresetResolver.Resolve(low, new Dictionary<string, string> { [PresetResolver.MetricsIntervalKey] = "0" }),
            "java", loader, "0.19.5");
        Assert.Null(ServerPropertiesFile.Load(propertiesPath).Get(RconSettings.EnableKey));

        ServerLaunch.Apply(dir, PresetResolver.Resolve(low, null), "java", loader, "0.19.5");
        Assert.NotNull(RconSettings.Read(dir));
    }
}

public class RconClientTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly FakeRconServer server = new("secret");

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    [Fact]
    public async Task AuthenticatesAndExecutesCommand()
    {
        server.Responses["list"] = "There are 0 of a max of 8 players online: ";
        await using var client = await RconClient.ConnectAsync(server.Endpoint, Timeout);
        Assert.Equal("There are 0 of a max of 8 players online: ", await client.ExecuteAsync("list", Timeout));
        Assert.Equal(["list"], server.Commands);
    }

    // 4095 + 한글(3바이트): 첫 조각 경계가 '한' 글자 중간에 걸린다.
    [Theory]
    [InlineData(4095, "한글 끝")]
    [InlineData(4096, "")]
    [InlineData(8192, "x")]
    public async Task JoinsResponseSplitAcrossPackets(int asciiLength, string tail)
    {
        var response = new string('a', asciiLength) + tail;
        server.Responses["spark tps"] = response;
        server.Responses["list"] = "ok";
        await using var client = await RconClient.ConnectAsync(server.Endpoint, Timeout);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(response, await client.ExecuteAsync("spark tps", Timeout));
            // 다음 명령의 응답이 섞이지 않는다.
            Assert.Equal("ok", await client.ExecuteAsync("list", Timeout));
        }
        Assert.Equal(0, server.DroppedForCoalescedPackets);
    }

    [Fact]
    public async Task ManyCommandsKeepOneConnection()
    {
        server.Responses["tick query"] = "Average time per tick: 1.0ms";
        server.Responses["list"] = "There are 0 of a max of 8 players online: ";
        await using var client = await RconClient.ConnectAsync(server.Endpoint, Timeout);
        for (var i = 0; i < 50; i++)
        {
            await client.ExecuteAsync("tick query", Timeout);
            await client.ExecuteAsync("list", Timeout);
        }
        Assert.Equal(100, server.Commands.Count);
        Assert.Equal(0, server.DroppedForCoalescedPackets);
    }

    [Fact]
    public async Task FakeServerDropsCoalescedPacketsLikeMinecraft()
    {
        // 가짜 서버가 실서버의 제약(한 번 읽은 바이트 = 패킷 하나)을 흉내 내는지 확인한다.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port);
        var stream = tcp.GetStream();
        var auth = FakeRconServer.Packet(1, RconClient.TypeAuth, "secret");
        var command = FakeRconServer.Packet(2, RconClient.TypeCommand, "list");
        await stream.WriteAsync(auth.Concat(command).ToArray());
        var buffer = new byte[64];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            total += read;
        Assert.Equal(0, total);
        Assert.Equal(1, server.DroppedForCoalescedPackets);
    }

    [Fact]
    public async Task WrongPasswordThrows()
    {
        await Assert.ThrowsAsync<RconAuthenticationException>(() =>
            RconClient.ConnectAsync(server.Endpoint with { Password = "wrong" }, Timeout));
    }

    [Fact]
    public async Task RejectsTooLongCommand()
    {
        await using var client = await RconClient.ConnectAsync(server.Endpoint, Timeout);
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteAsync(new string('x', RconClient.MaxCommandBytes + 1), Timeout));
    }

    [Fact]
    public async Task ClosedConnectionIsReportedAsIoError()
    {
        await using var client = await RconClient.ConnectAsync(server.Endpoint, Timeout);
        server.DropConnections();
        await Assert.ThrowsAnyAsync<IOException>(() => client.ExecuteAsync("list", Timeout));
    }
}

/// <summary>
/// Minecraft RCON 서버 동작을 흉내 낸다: 한 번 읽은 바이트가 패킷 하나와 다르면 연결을 끊고, 인증, 응답은 본문 4096바이트 단위로 나눠 보내며,
/// 알 수 없는 종류에는 "Unknown request"로 답한다.
/// </summary>
internal sealed class FakeRconServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string password;
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpClient> clients = [];
    private readonly Task acceptLoop;

    public FakeRconServer(string password)
    {
        this.password = password;
        listener.Start();
        acceptLoop = AcceptAsync();
    }

    public RconEndpoint Endpoint => new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, password);
    public Dictionary<string, string> Responses { get; } = [];
    public List<string> Commands { get; } = [];
    public int DroppedForCoalescedPackets { get; private set; }

    public void DropConnections()
    {
        lock (clients)
        {
            foreach (var c in clients)
                c.Dispose();
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                lock (clients)
                    clients.Add(client);
                _ = HandleAsync(client);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var buffer = new byte[1460];
            while (true)
            {
                // Minecraft RconClient: read(buf, 0, 1460) 한 번 → 길이 필드가 (읽은 바이트 - 4)와 다르면 연결 종료
                var read = await stream.ReadAsync(buffer, stop.Token);
                if (read < 10)
                    return;
                var length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                if (length != read - 4)
                {
                    DroppedForCoalescedPackets++;
                    return;
                }
                var id = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4));
                var type = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
                var body = Encoding.UTF8.GetString(buffer, 12, read - 14);

                if (type == RconClient.TypeAuth)
                {
                    await SendAsync(stream, body == password ? id : -1, RconClient.TypeCommand, "");
                }
                else if (type == RconClient.TypeCommand)
                {
                    Commands.Add(body);
                    var bytes = Encoding.UTF8.GetBytes(Responses.GetValueOrDefault(body, "Unknown or incomplete command, see below for error"));
                    var offset = 0;
                    do
                    {
                        var size = Math.Min(RconClient.ResponseChunkBytes, bytes.Length - offset);
                        await stream.WriteAsync(Packet(id, RconClient.TypeResponse, bytes.AsSpan(offset, size)));
                        offset += size;
                    }
                    while (offset < bytes.Length);
                }
                else
                {
                    await SendAsync(stream, id, RconClient.TypeResponse, $"Unknown request {type:x}");
                }
            }
        }
        catch (Exception)
        {
            // 연결 종료
        }
    }

    private static async Task SendAsync(NetworkStream stream, int id, int type, string body) =>
        await stream.WriteAsync(Packet(id, type, body));

    public static byte[] Packet(int id, int type, string body) => Packet(id, type, Encoding.UTF8.GetBytes(body));

    public static byte[] Packet(int id, int type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[14 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        DropConnections();
        await acceptLoop;
        stop.Dispose();
    }
}
