using Builder.Core.Rcon;

namespace Builder.Core.Server;

/// <summary>builder.json의 crossplay 항목. 베드락판 접속(Geyser + Floodgate) 설정.</summary>
public sealed class CrossplaySettings
{
    public bool Enabled { get; set; }
    public int BedrockPort { get; set; } = Crossplay.DefaultBedrockPort;
}

/// <summary>
/// Java판 서버에 베드락판 클라이언트가 접속하도록 Geyser(프로토콜 변환)와 Floodgate(Java 계정 없이 인증)를 설치한다.
/// 두 모드는 프리셋 묶음과 별개로 켜고 끄며, 설치·정리는 다른 모드처럼 <see cref="Mods.ModInstaller"/>가 맡는다.
/// </summary>
public static class Crossplay
{
    public const int DefaultBedrockPort = 19132;
    public const string GeyserModId = "geyser";
    public const string FloodgateModId = "floodgate";
    public static readonly IReadOnlyList<string> ModIds = [GeyserModId, FloodgateModId];

    /// <summary>
    /// Geyser는 GEYSER_ + 설정 경로(대문자, '-'는 '_') 환경 변수로 설정값을 덮어쓴다. 덮어쓴 값은 config.yml에 기록되지 않는다.
    /// 그래서 설정 파일(첫 실행 때 생성, 버전마다 형식이 바뀔 수 있음)을 고치지 않고 시작 스크립트와 실행 환경으로 포트를 넘긴다.
    /// (Geyser 2.12 EnvironmentOverrides, 설정 경로 bedrock.port — 2026-10-27 확인)
    /// </summary>
    public const string BedrockPortEnvironmentKey = "GEYSER_BEDROCK_PORT";

    /// <summary>
    /// Fabric용 Geyser는 Floodgate가 있어도 생성 설정이 java.auth-type: online이다 (2026-10-27 실서버 확인).
    /// 그대로 두면 베드락 사용자에게 Java 계정을 요구하므로 floodgate로 지정한다.
    /// </summary>
    public const string AuthTypeEnvironmentKey = "GEYSER_JAVA_AUTH_TYPE";

    /// <summary>프리셋 모드에 크로스플레이 모드를 더한 설치 대상 목록.</summary>
    public static IReadOnlyList<string> WantedMods(IReadOnlyList<string> presetMods, ServerInstanceConfig config) =>
        config.Crossplay.Enabled ? [.. presetMods, .. ModIds.Where(id => !presetMods.Contains(id))] : presetMods;

    /// <summary>서버 프로세스에 넘길 환경 변수. 크로스플레이가 꺼져 있으면 비어 있다.</summary>
    public static IReadOnlyDictionary<string, string> Environment(ServerInstanceConfig config) =>
        config.Crossplay.Enabled
            ? new Dictionary<string, string>
            {
                [BedrockPortEnvironmentKey] = config.Crossplay.BedrockPort.ToString(),
                [AuthTypeEnvironmentKey] = "floodgate",
            }
            : new Dictionary<string, string>();

    /// <summary>
    /// 베드락 포트를 확인한다. Geyser는 이 포트를 UDP(RakNet)와 TCP(내장 시그널링)로 함께 쓰므로
    /// Java 게임 포트·RCON 포트와 같으면 안 된다.
    /// </summary>
    public static void ValidatePort(int port, ServerPropertiesFile properties)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentException($"베드락 포트는 1~65535 사이여야 합니다: {port}");

        var gamePort = int.TryParse(properties.Get("server-port"), out var g) ? g : 25565;
        if (port == gamePort)
            throw new ArgumentException($"베드락 포트 {port}이(가) Java 게임 포트(server-port)와 같습니다. 다른 포트를 지정하세요.");
        if (RconSettings.Read(properties) is { } rcon && rcon.Port == port)
            throw new ArgumentException($"베드락 포트 {port}이(가) RCON 포트(rcon.port)와 같습니다. 다른 포트를 지정하세요.");
    }

    /// <summary>베드락 사용자는 접속할 때마다 Geyser가 프로토콜을 변환하므로, 낮은 단계 프리셋에서는 부담이 커질 수 있다.</summary>
    public static bool IsLightPreset(string presetId) => presetId is "low" or "minimum";
}
