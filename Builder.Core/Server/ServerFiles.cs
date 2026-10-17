using System.Text;
using System.Text.Json;
using Builder.Core.Http;
using Builder.Core.Mods;
using Builder.Core.Presets;

namespace Builder.Core.Server;

/// <summary>서버 폴더의 builder.json. 서버 구성의 원본이다.</summary>
public sealed class ServerInstanceConfig
{
    public const string FileName = "builder.json";

    private static readonly JsonSerializerOptions Options = new(BuilderHttp.Json)
    {
        WriteIndented = true,
        // 사람이 직접 편집하는 파일이므로 경로의 '+'나 한글을 \uXXXX로 바꾸지 않는다.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new LenientStringConverter() },
    };

    public int SchemaVersion { get; init; } = 1;
    public required string MinecraftVersion { get; init; }
    public required string LoaderVersion { get; init; }
    public required string InstallerVersion { get; init; }
    public int JavaMajorVersion { get; init; }
    public string? JavaPath { get; set; }
    public string Preset { get; set; } = "low";
    public Dictionary<string, string> Overrides { get; init; } = new(StringComparer.Ordinal);

    /// <summary>false면 프리셋 모드를 자동으로 설치·정리하지 않는다 (create --no-mods).</summary>
    public bool ManageMods { get; set; } = true;

    /// <summary>정식 버전이 없을 때 베타·알파 버전 설치를 허용할지 여부.</summary>
    public bool AllowUnstableMods { get; set; }

    /// <summary>빌더가 설치한 모드 기록. 사용자가 직접 넣은 jar는 여기에 없다.</summary>
    public List<InstalledMod> Mods { get; init; } = [];

    public static bool Exists(string serverDir) => File.Exists(Path.Combine(serverDir, FileName));

    public static ServerInstanceConfig Load(string serverDir)
    {
        var path = Path.Combine(serverDir, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"빌더로 만든 서버 폴더가 아닙니다 ({FileName} 없음): {serverDir}", path);
        return JsonSerializer.Deserialize<ServerInstanceConfig>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"{FileName}을(를) 읽을 수 없습니다: {path}");
    }

    public void Save(string serverDir) =>
        File.WriteAllText(Path.Combine(serverDir, FileName), JsonSerializer.Serialize(this, Options) + "\n", new UTF8Encoding(false));
}

/// <summary>
/// server.properties를 줄 단위로 다룬다. 기존 주석·순서·다른 키를 보존하고 지정한 키만 바꾼다.
/// 값 이스케이프는 하지 않으므로 프리셋 값은 ASCII로 유지한다.
/// </summary>
public sealed class ServerPropertiesFile
{
    public const string FileName = "server.properties";

    private readonly List<string> lines;

    public ServerPropertiesFile(IEnumerable<string> lines)
    {
        this.lines = lines.ToList();
    }

    public IReadOnlyList<string> Lines => lines;

    public static ServerPropertiesFile Load(string path) =>
        new(File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : []);

    public void Save(string path) =>
        File.WriteAllLines(path, lines, new UTF8Encoding(false));

    public string? Get(string key)
    {
        var index = IndexOf(key);
        return index < 0 ? null : lines[index][(lines[index].IndexOfAny(['=', ':']) + 1)..];
    }

    public void Set(string key, string value)
    {
        var line = $"{key}={value}";
        var index = IndexOf(key);
        if (index >= 0)
            lines[index] = line;
        else
            lines.Add(line);
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryGetKey(lines[i], out var existing) && existing == key)
                return i;
        }
        return -1;
    }

    private static bool TryGetKey(string line, out string key)
    {
        key = "";
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
            return false;
        var separator = trimmed.IndexOfAny(['=', ':']);
        if (separator <= 0)
            return false;
        key = trimmed[..separator].TrimEnd();
        return true;
    }
}

public static class EulaFile
{
    public const string FileName = "eula.txt";
    public const string EulaUrl = "https://aka.ms/MinecraftEULA";

    /// <summary>사용자가 EULA에 명시적으로 동의한 경우에만 호출한다.</summary>
    public static void WriteAccepted(string serverDir) =>
        File.WriteAllText(
            Path.Combine(serverDir, FileName),
            $"#By changing the setting below to TRUE you are indicating your agreement to our EULA ({EulaUrl}).\n" +
            $"#Accepted via MinecraftHybridBuilder at {DateTimeOffset.Now:O}\n" +
            "eula=true\n");

    public static bool IsAccepted(string serverDir)
    {
        var path = Path.Combine(serverDir, FileName);
        return File.Exists(path) && File.ReadLines(path)
            .Any(l => l.Trim().Equals("eula=true", StringComparison.OrdinalIgnoreCase));
    }
}

public static class ServerLaunch
{
    public const string LauncherJar = "fabric-server-launch.jar";

    /// <summary>-Xms = -Xmx (설계서 4장), 프리셋 JVM 플래그, Fabric 런처 실행.</summary>
    public static IReadOnlyList<string> BuildArguments(ResolvedPreset preset) =>
    [
        $"-Xms{preset.HeapMb}M",
        $"-Xmx{preset.HeapMb}M",
        .. preset.JvmFlags,
        "-jar", LauncherJar, "nogui",
    ];

    /// <summary>프리셋을 server.properties와 실행 스크립트(start.bat / start.sh)에 반영한다.</summary>
    public static void Apply(string serverDir, ResolvedPreset preset, string javaPath)
    {
        var propertiesPath = Path.Combine(serverDir, ServerPropertiesFile.FileName);
        var properties = ServerPropertiesFile.Load(propertiesPath);
        foreach (var (key, value) in preset.ServerProperties)
            properties.Set(key, value);
        properties.Save(propertiesPath);

        WriteScripts(serverDir, javaPath, BuildArguments(preset));
    }

    private static void WriteScripts(string serverDir, string javaPath, IReadOnlyList<string> arguments)
    {
        var args = string.Join(' ', arguments);
        var utf8 = new UTF8Encoding(false);

        File.WriteAllText(Path.Combine(serverDir, "start.bat"),
            "@echo off\r\n" +
            "rem MinecraftHybridBuilder가 생성한 파일입니다. 'mchb configure'를 실행하면 다시 생성됩니다.\r\n" +
            "chcp 65001 > nul\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            $"\"{javaPath}\" {args}\r\n" +
            "pause\r\n", utf8);

        var shPath = Path.Combine(serverDir, "start.sh");
        File.WriteAllText(shPath,
            "#!/bin/sh\n" +
            "# MinecraftHybridBuilder가 생성한 파일입니다. 'mchb configure'를 실행하면 다시 생성됩니다.\n" +
            "cd \"$(dirname \"$0\")\" || exit 1\n" +
            $"exec \"{javaPath}\" {args}\n", utf8);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(shPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
