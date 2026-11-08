using System.Globalization;
using System.Text.RegularExpressions;

namespace Builder.Core.Server;

/// <summary>서버 콘솔 출력 한 줄에서 읽어 낸 사건.</summary>
public abstract record ConsoleEvent;

public sealed record PlayerJoined(string Name) : ConsoleEvent;

public sealed record PlayerLeft(string Name) : ConsoleEvent;

/// <summary>월드 불러오기가 끝나 접속을 받을 수 있게 된 시점 (<c>Done (12.3s)!</c>).</summary>
public sealed record ServerReady(double? StartupSeconds) : ConsoleEvent;

/// <summary>서버가 종료(월드 저장)를 시작한 시점.</summary>
public sealed record ServerStopping : ConsoleEvent;

/// <summary>
/// 서버 콘솔 출력 해석과 콘솔 명령 만들기. 메시지는 서버 언어(en_us)로 나온다.
/// 줄 머리는 바닐라·Fabric <c>[시각] [Server thread/INFO]: </c>, NeoForge <c>[시각] [Server thread/INFO] [minecraft/...]: </c> 형식이다.
/// 채팅(<c>&lt;이름&gt; ...</c>)이 사건으로 잘못 읽히지 않도록, 줄 머리 바로 뒤에 오는 메시지만 본다.
/// </summary>
public static partial class ServerConsole
{
    /// <summary>바로 보낼 수 있게 다듬은 명령. 앞의 '/'는 뗀다. 줄바꿈이 있으면 명령 두 개가 되므로 거부한다.</summary>
    public static string NormalizeCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Contains('\n') || command.Contains('\r'))
            throw new ArgumentException("명령에 줄바꿈을 넣을 수 없습니다.", nameof(command));
        var trimmed = command.Trim().TrimStart('/').Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("명령이 비어 있습니다.", nameof(command));
        return trimmed;
    }

    public static ConsoleEvent? Parse(string line)
    {
        var message = ServerThreadMessage().Match(line);
        if (!message.Success)
            return null;
        var text = message.Groups["msg"].Value;

        var player = PlayerChange().Match(text);
        if (player.Success)
            return player.Groups["what"].Value == "joined"
                ? new PlayerJoined(player.Groups["name"].Value)
                : new PlayerLeft(player.Groups["name"].Value);

        var done = Done().Match(text);
        if (done.Success)
            return new ServerReady(double.TryParse(done.Groups["sec"].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds) ? seconds : null);

        // 서버 종료 로그와 stop 명령 응답
        return text is "Stopping server" or "Stopping the server" ? new ServerStopping() : null;
    }

    /// <summary>Java판 이름(영문·숫자·밑줄 16자 이하)과 Floodgate 베드락 이름(앞에 '.')만 허용한다. 명령 끼워 넣기를 막는다.</summary>
    public static bool IsValidPlayerName(string? name) => name is not null && PlayerName().IsMatch(name);

    public static string Kick(string name) => $"kick {RequireName(name)}";

    public static string Op(string name) => $"op {RequireName(name)}";

    public static string Deop(string name) => $"deop {RequireName(name)}";

    public static string WhitelistAdd(string name) => $"whitelist add {RequireName(name)}";

    public static string WhitelistRemove(string name) => $"whitelist remove {RequireName(name)}";

    public const string SaveAll = "save-all";

    public const string Stop = "stop";

    private static string RequireName(string name) => IsValidPlayerName(name)
        ? name
        : throw new ArgumentException($"플레이어 이름이 올바르지 않습니다: '{name}' (영문·숫자·밑줄 16자 이하, 베드락은 앞에 '.')", nameof(name));

    [GeneratedRegex(@"^\[[^\]]*\] \[Server thread/INFO\](?:: | \([^)]*\) | \[[^\]]*\]: )(?<msg>.*?)\s*$")]
    private static partial Regex ServerThreadMessage();

    [GeneratedRegex(@"^(?<name>\.?[A-Za-z0-9_]{1,16}) (?<what>joined|left) the game$")]
    private static partial Regex PlayerChange();

    [GeneratedRegex(@"^Done \((?<sec>[\d.,]+)s\)! For help, type ""help""")]
    private static partial Regex Done();

    [GeneratedRegex(@"^\.?[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();
}
