using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Builder.Core.Server;

namespace Builder.Core.Rcon;

/// <summary>접속할 RCON 주소와 비밀번호. 비밀번호는 server.properties에만 있고 builder.json에는 쓰지 않는다.</summary>
public sealed record RconEndpoint(string Host, int Port, string Password);

/// <summary>server.properties의 RCON 항목을 다룬다.</summary>
public static class RconSettings
{
    public const string EnableKey = "enable-rcon";
    public const string PortKey = "rcon.port";
    public const string PasswordKey = "rcon.password";
    public const string BroadcastKey = "broadcast-rcon-to-ops";

    /// <summary>게임 포트에서 이만큼 떨어진 포트를 쓴다. 기본 게임 포트 25565 → Minecraft 기본 RCON 포트 25575.</summary>
    public const int PortOffset = 10;

    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>
    /// 지표 수집용으로 RCON을 켠다. 사용자가 정한 포트·비밀번호는 유지하고, 없을 때만 채운다.
    /// 바뀐 항목이 있으면 true.
    /// </summary>
    public static bool EnsureEnabled(ServerPropertiesFile properties)
    {
        var changed = false;
        void SetIfDifferent(string key, string value)
        {
            if (properties.Get(key) == value)
                return;
            properties.Set(key, value);
            changed = true;
        }

        SetIfDifferent(EnableKey, "true");
        if (!int.TryParse(properties.Get(PortKey), out var port) || port is <= 0 or > 65535)
            SetIfDifferent(PortKey, DefaultPort(properties).ToString());
        if (string.IsNullOrWhiteSpace(properties.Get(PasswordKey)))
            SetIfDifferent(PasswordKey, RandomNumberGenerator.GetString(PasswordAlphabet, 24));
        // 기본값 true면 수집 명령의 결과가 접속한 OP 채팅에 계속 표시된다.
        SetIfDifferent(BroadcastKey, "false");
        return changed;
    }

    /// <summary>RCON이 켜져 있고 비밀번호가 있으면 접속 정보를 돌려준다.</summary>
    public static RconEndpoint? Read(ServerPropertiesFile properties)
    {
        if (!string.Equals(properties.Get(EnableKey)?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return null;
        var password = properties.Get(PasswordKey)?.Trim();
        if (string.IsNullOrEmpty(password))
            return null;
        var port = int.TryParse(properties.Get(PortKey), out var p) ? p : DefaultPort(properties);
        // server-ip가 비어 있으면 모든 주소에서 열리므로 같은 PC의 루프백으로 접속한다.
        var host = properties.Get("server-ip")?.Trim();
        return new RconEndpoint(string.IsNullOrEmpty(host) ? "127.0.0.1" : host, port, password);
    }

    public static RconEndpoint? Read(string serverDir) =>
        Read(ServerPropertiesFile.Load(Path.Combine(serverDir, ServerPropertiesFile.FileName)));

    private static int DefaultPort(ServerPropertiesFile properties)
    {
        var gamePort = int.TryParse(properties.Get("server-port"), out var p) && p is > 0 and <= 65535 - PortOffset ? p : 25565;
        return gamePort + PortOffset;
    }
}

public sealed class RconAuthenticationException(string message) : Exception(message);

/// <summary>
/// Source RCON 프로토콜 클라이언트. 패킷: 길이(int32 LE) + 요청 ID + 종류 + 본문(UTF-8) + 널 2바이트.
/// 한 연결에서 명령을 하나씩 보낸다 (동시 호출은 직렬화한다).
/// </summary>
/// <remarks>
/// Minecraft RCON은 소켓에서 한 번 읽은 바이트가 패킷 하나와 정확히 같지 않으면 연결을 끊는다.
/// 그래서 패킷을 연달아 보내지 않고, 항상 응답을 받은 뒤 다음 패킷을 보낸다 (2026-10-23 실서버 26.3에서 확인).
/// </remarks>
public sealed class RconClient : IAsyncDisposable
{
    internal const int TypeResponse = 0;
    internal const int TypeCommand = 2;
    internal const int TypeAuth = 3;

    /// <summary>Minecraft가 받는 본문 최대 길이(1446바이트 패킷 기준). 넘는 명령은 보내지 않는다.</summary>
    public const int MaxCommandBytes = 1446 - 10;

    /// <summary>Minecraft는 긴 응답을 본문 4096바이트 단위로 나눠 보낸다. 이보다 짧은 조각이 마지막이다.</summary>
    internal const int ResponseChunkBytes = 4096;

    private const int MaxPacketLength = 1024 * 1024;

    private readonly TcpClient tcp;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int nextId = 1;

    private RconClient(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    /// <summary>접속하고 인증한다. 비밀번호가 틀리면 <see cref="RconAuthenticationException"/>.</summary>
    public static async Task<RconClient> ConnectAsync(RconEndpoint endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var tcp = new TcpClient { NoDelay = true };
        RconClient? client = null;
        try
        {
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, timeoutCts.Token);
            client = new RconClient(tcp);

            var id = client.NextId();
            await client.WritePacketAsync(id, TypeAuth, endpoint.Password, timeoutCts.Token);
            // 인증 응답 앞에 빈 응답 패킷이 오는 서버가 있어 종류가 2인 패킷까지 읽는다.
            while (true)
            {
                var (responseId, type, _) = await client.ReadPacketAsync(timeoutCts.Token);
                if (type != TypeCommand)
                    continue;
                if (responseId == -1)
                    throw new RconAuthenticationException("RCON 비밀번호가 맞지 않습니다 (server.properties의 rcon.password 확인).");
                if (responseId == id)
                    return client;
            }
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync();
            else
                tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 명령을 실행하고 응답 본문을 돌려준다. 응답 조각이 꽉 찬(4096바이트) 경우에만 뒤에 조각이 더 있을 수 있으므로,
    /// 그때 확인용 패킷을 보내고 그 응답이 올 때까지 읽는다 (서버는 응답을 다 보낸 뒤 다음 요청을 읽는다).
    /// </summary>
    public async Task<string> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(command) > MaxCommandBytes)
            throw new ArgumentException($"RCON 명령이 너무 깁니다 (최대 {MaxCommandBytes}바이트).", nameof(command));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        await gate.WaitAsync(timeoutCts.Token);
        try
        {
            var id = NextId();
            await WritePacketAsync(id, TypeCommand, command, timeoutCts.Token);

            // 조각 경계가 UTF-8 문자 중간일 수 있어 바이트로 모은 뒤 한 번에 디코딩한다.
            var body = new MemoryStream();
            byte[] chunk;
            while (true)
            {
                (var responseId, _, chunk) = await ReadPacketAsync(timeoutCts.Token);
                if (responseId == id)
                    break;
            }
            body.Write(chunk);

            if (chunk.Length == ResponseChunkBytes)
            {
                var sentinel = NextId();
                await WritePacketAsync(sentinel, TypeResponse, "", timeoutCts.Token);
                while (true)
                {
                    var (responseId, _, more) = await ReadPacketAsync(timeoutCts.Token);
                    if (responseId == sentinel)
                        break;
                    if (responseId == id)
                        body.Write(more);
                }
            }
            return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
        }
        finally
        {
            gate.Release();
        }
    }

    private int NextId()
    {
        // -1은 인증 실패 응답에 쓰이므로 양수만 쓴다.
        var id = nextId++;
        if (nextId == int.MaxValue)
            nextId = 1;
        return id;
    }

    private async Task WritePacketAsync(int id, int type, string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + payload.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        payload.CopyTo(packet, 12);
        await stream.WriteAsync(packet, ct);
    }

    private async Task<(int Id, int Type, byte[] Body)> ReadPacketAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 10 or > MaxPacketLength)
            throw new IOException($"RCON 패킷 길이가 올바르지 않습니다: {length}");

        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        var id = BinaryPrimitives.ReadInt32LittleEndian(data);
        var type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        // 본문 뒤에 널 2바이트가 붙는다.
        var body = data.AsSpan(8, Math.Max(0, length - 10)).ToArray();
        return (id, type, body);
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        tcp.Dispose();
        gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
