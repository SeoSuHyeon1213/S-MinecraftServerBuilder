using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using Builder.Core.Http;
using Builder.Core.Minecraft;

namespace Builder.Core.Loaders;

/// <summary>지원하는 모드 로더 ID. builder.json의 loader 값과 Modrinth의 loader 이름이 같다.</summary>
public static class ModLoaders
{
    public const string Fabric = "fabric";
    public const string NeoForge = "neoforge";

    public static IReadOnlyList<string> All { get; } = [Fabric, NeoForge];

    /// <summary>영문 ID와 한글 이름을 모두 받는다.</summary>
    public static string Normalize(string value) => value.Trim().ToLowerInvariant() switch
    {
        "fabric" or "페브릭" or "패브릭" => Fabric,
        "neoforge" or "neo-forge" or "neo" or "네오포지" => NeoForge,
        _ => throw new ArgumentException($"지원하지 않는 모드 로더: {value} (사용 가능: fabric/페브릭, neoforge/네오포지)"),
    };

    public static string DisplayName(string id) => id switch
    {
        Fabric => "Fabric",
        NeoForge => "NeoForge",
        _ => id,
    };
}

/// <param name="LoaderVersion">로더 버전 (Fabric 로더 0.19.5, NeoForge 26.3.0.90 등)</param>
/// <param name="InstallerVersion">Fabric 인스톨러 버전. NeoForge는 로더 버전과 인스톨러가 같으므로 null.</param>
public sealed record LoaderPlan(string LoaderVersion, string? InstallerVersion);

/// <summary>
/// 모드 로더별 서버 설치·실행 방법. 서버 생성·전환·실행 로직은 이 규격만 사용한다.
/// 로더를 추가할 때는 이 인터페이스를 구현하고 ServerService의 목록에 등록한다.
/// </summary>
public interface IServerLoader
{
    string Id { get; }

    /// <summary>이 로더가 안정 버전을 제공하는 가장 최신 Minecraft 버전.</summary>
    Task<string> GetLatestGameVersionAsync(CancellationToken ct = default);

    /// <summary>게임 버전에 맞는 로더 버전을 고른다. 지원하지 않으면 InvalidOperationException.</summary>
    Task<LoaderPlan> ResolveAsync(string gameVersion, string? requestedLoaderVersion, CancellationToken ct = default);

    /// <summary>서버 폴더에 로더 서버 파일을 설치한다. 기존 월드·설정 파일은 건드리지 않는다.</summary>
    Task InstallAsync(string serverDir, string gameVersion, LoaderPlan plan, string javaPath,
        IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>JVM 옵션 뒤에 붙는 실행 대상 인자. <paramref name="windows"/>는 스크립트 대상 OS.</summary>
    IReadOnlyList<string> LaunchArguments(string loaderVersion, bool windows);
}

public sealed class FabricServerLoader(HttpClient http) : IServerLoader
{
    public const string LauncherJar = "fabric-server-launch.jar";

    private readonly FabricMetaClient meta = new(http);

    public string Id => ModLoaders.Fabric;

    public async Task<string> GetLatestGameVersionAsync(CancellationToken ct = default) =>
        (await meta.GetGameVersionsAsync(ct)).FirstOrDefault(v => v.Stable)?.Version
        ?? throw new InvalidOperationException("Fabric 메타 API에서 안정 버전을 찾지 못했습니다.");

    public async Task<LoaderPlan> ResolveAsync(string gameVersion, string? requestedLoaderVersion, CancellationToken ct = default)
    {
        var loaders = await meta.GetLoaderVersionsAsync(gameVersion, ct);
        if (loaders.Count == 0)
            throw new InvalidOperationException($"Fabric이 Minecraft {gameVersion}을(를) 지원하지 않습니다. 'versions' 명령으로 목록을 확인하세요.");

        var loader = requestedLoaderVersion ?? FabricMetaClient.PickLatestStable(loaders)!.Version;
        if (!loaders.Any(l => l.Version == loader))
            throw new InvalidOperationException($"Minecraft {gameVersion}용 Fabric 로더 {loader}이(가) 없습니다.");

        var installer = FabricMetaClient.PickLatestStable(await meta.GetInstallerVersionsAsync(ct))?.Version
            ?? throw new InvalidOperationException("Fabric 인스톨러 버전을 조회하지 못했습니다.");
        return new LoaderPlan(loader, installer);
    }

    public Task InstallAsync(string serverDir, string gameVersion, LoaderPlan plan, string javaPath,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        meta.DownloadServerLauncherAsync(gameVersion, plan.LoaderVersion,
            plan.InstallerVersion ?? throw new InvalidOperationException("Fabric 인스톨러 버전이 없습니다."),
            Path.Combine(serverDir, LauncherJar), progress, ct);

    public IReadOnlyList<string> LaunchArguments(string loaderVersion, bool windows) => ["-jar", LauncherJar, "nogui"];
}

/// <summary>
/// NeoForge 서버. maven.neoforged.net의 인스톨러를 내려받아 --install-server로 설치하고,
/// 인스톨러가 만든 인자 파일(@libraries/.../win_args.txt, unix_args.txt)로 실행한다.
/// </summary>
public sealed class NeoForgeServerLoader(HttpClient http, Action<string> log) : IServerLoader
{
    public const string MavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    public const string InstallerJar = "neoforge-installer.jar";

    public string Id => ModLoaders.NeoForge;

    public async Task<IReadOnlyList<string>> GetAllVersionsAsync(CancellationToken ct = default) =>
        NeoForgeVersions.ParseMetadata(await http.GetStringAsync($"{MavenBase}/maven-metadata.xml", ct));

    public async Task<string> GetLatestGameVersionAsync(CancellationToken ct = default)
    {
        var latest = NeoForgeVersions.Summarize(await GetAllVersionsAsync(ct)).FirstOrDefault(v => v.Stable);
        return latest.Game ?? throw new InvalidOperationException("NeoForge 안정 버전을 찾지 못했습니다.");
    }

    public async Task<LoaderPlan> ResolveAsync(string gameVersion, string? requestedLoaderVersion, CancellationToken ct = default)
    {
        var matching = NeoForgeVersions.ForGame(await GetAllVersionsAsync(ct), gameVersion);
        if (matching.Count == 0)
            throw new InvalidOperationException(
                $"NeoForge가 Minecraft {gameVersion}을(를) 지원하지 않습니다. 'versions --modloader neoforge'로 목록을 확인하세요.");

        if (requestedLoaderVersion is not null)
        {
            if (!matching.Contains(requestedLoaderVersion))
                throw new InvalidOperationException($"Minecraft {gameVersion}용 NeoForge {requestedLoaderVersion}이(가) 없습니다.");
            return new LoaderPlan(requestedLoaderVersion, null);
        }

        var pick = matching.FirstOrDefault(NeoForgeVersions.IsStable) ?? matching[0];
        if (!NeoForgeVersions.IsStable(pick))
            log($"주의: Minecraft {gameVersion}용 NeoForge는 아직 베타 버전({pick})만 있습니다.");
        return new LoaderPlan(pick, null);
    }

    public async Task InstallAsync(string serverDir, string gameVersion, LoaderPlan plan, string javaPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var version = plan.LoaderVersion;
        var installerPath = Path.Combine(serverDir, InstallerJar);
        await BuilderHttp.DownloadFileAsync(http, $"{MavenBase}/{version}/neoforge-{version}-installer.jar",
            installerPath, progress, ct: ct);

        // 인스톨러는 자체 run.bat / run.sh를 만든다. 빌더의 start 스크립트와 헷갈리지 않도록 이번에 생긴 것만 지운다.
        string[] installerScripts = ["run.bat", "run.sh"];
        var preexisting = installerScripts.Where(f => File.Exists(Path.Combine(serverDir, f))).ToHashSet();

        log("NeoForge 서버 설치 중 (Minecraft 서버 파일과 라이브러리를 내려받으므로 몇 분 걸릴 수 있습니다)...");
        var psi = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 설치 위치를 생략하면 현재 폴더(WorkingDirectory)에 설치한다.
        foreach (var argument in new[] { "-jar", InstallerJar, "--install-server" })
            psi.ArgumentList.Add(argument);

        var output = new ConcurrentQueue<string>();
        int exitCode;
        using (var process = Process.Start(psi) ?? throw new InvalidOperationException("NeoForge 인스톨러를 시작하지 못했습니다."))
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            exitCode = process.ExitCode;
        }

        File.Delete(installerPath);
        foreach (var script in installerScripts.Where(s => !preexisting.Contains(s)))
            File.Delete(Path.Combine(serverDir, script));

        var argsFile = Path.Combine(serverDir, ArgsFile(version, OperatingSystem.IsWindows()));
        if (exitCode != 0 || !File.Exists(argsFile))
        {
            var tail = string.Join(Environment.NewLine, output.TakeLast(15));
            throw new InvalidOperationException($"NeoForge 설치에 실패했습니다 (종료 코드 {exitCode}).{Environment.NewLine}{tail}");
        }
    }

    public IReadOnlyList<string> LaunchArguments(string loaderVersion, bool windows) =>
        [$"@{ArgsFile(loaderVersion, windows).Replace('\\', '/')}", "nogui"];

    /// <summary>인스톨러가 만드는 JVM 인자 파일의 서버 폴더 기준 상대 경로.</summary>
    public static string ArgsFile(string loaderVersion, bool windows) =>
        Path.Combine("libraries", "net", "neoforged", "neoforge", loaderVersion, windows ? "win_args.txt" : "unix_args.txt");
}

/// <summary>
/// NeoForge 버전 번호 규칙 (네트워크 없이 테스트할 수 있도록 분리).
/// - Minecraft 1.X.Y → NeoForge X.Y.빌드 (예: 1.21.1 → 21.1.258, 1.21 → 21.0.x)
/// - Minecraft 연도 버전 YY.N[.P] → NeoForge YY.N.P.빌드 (예: 26.3 → 26.3.0.90, 26.1.2 → 26.1.2.116)
/// - "-beta" 등 접미사가 붙으면 불안정 버전이다.
/// </summary>
public static class NeoForgeVersions
{
    /// <summary>NeoForge 주 번호가 이 값 이상이면 연도 버전 체계(26.x~)로 본다. 1.x 체계는 20~21이다.</summary>
    private const int YearSchemeMajor = 25;

    public static IReadOnlyList<string> ParseMetadata(string xml) =>
        XDocument.Parse(xml).Descendants("version")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static bool IsStable(string version) => !version.Contains('-');

    /// <summary>Minecraft 버전에 해당하는 NeoForge 버전 접두사. 스냅샷 등 지원하지 않는 형식이면 null.</summary>
    public static string? Prefix(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length is < 2 or > 3 || parts.Any(p => !int.TryParse(p, out _)))
            return null;
        var patch = parts.Length == 3 ? parts[2] : "0";
        return parts[0] == "1"
            ? parts.Length == 2 ? $"{parts[1]}.0." : $"{parts[1]}.{patch}."
            : $"{parts[0]}.{parts[1]}.{patch}.";
    }

    /// <summary>NeoForge 버전이 대상으로 하는 Minecraft 버전. 해석할 수 없으면 null.</summary>
    public static string? GameVersionOf(string version)
    {
        var n = Numbers(version);
        if (n is null)
            return null;
        if (n[0] >= YearSchemeMajor)
            return n.Length != 4 ? null : n[2] == 0 ? $"{n[0]}.{n[1]}" : $"{n[0]}.{n[1]}.{n[2]}";
        return n.Length != 3 ? null : n[1] == 0 ? $"1.{n[0]}" : $"1.{n[0]}.{n[1]}";
    }

    /// <summary>게임 버전에 맞는 NeoForge 버전 목록 (최신순).</summary>
    public static IReadOnlyList<string> ForGame(IEnumerable<string> versions, string gameVersion)
    {
        var prefix = Prefix(gameVersion);
        if (prefix is null)
            return [];
        return versions
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal) && GameVersionOf(v) == Normalize(gameVersion))
            .OrderByDescending(v => v, Comparer)
            .ToList();
    }

    /// <summary>게임 버전별 대표 NeoForge 버전 (안정 버전 우선, 최신 게임 버전순).</summary>
    public static IReadOnlyList<(string Game, string Build, bool Stable)> Summarize(IEnumerable<string> versions) =>
        versions
            .Select(v => (Version: v, Game: GameVersionOf(v)))
            .Where(x => x.Game is not null)
            .GroupBy(x => x.Game!)
            .Select(g =>
            {
                var ordered = g.Select(x => x.Version).OrderByDescending(v => v, Comparer).ToList();
                var stable = ordered.FirstOrDefault(IsStable);
                return (Game: g.Key, Build: stable ?? ordered[0], Stable: stable is not null);
            })
            .OrderByDescending(x => x.Build, Comparer)
            .ToList();

    /// <summary>숫자 부분을 차례로 비교하고, 같으면 안정 버전을 더 높게 본다.</summary>
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create((a, b) =>
    {
        var x = Numbers(a) ?? [];
        var y = Numbers(b) ?? [];
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (x[i] != y[i])
                return x[i].CompareTo(y[i]);
        if (x.Length != y.Length)
            return x.Length.CompareTo(y.Length);
        return IsStable(a).CompareTo(IsStable(b));
    });

    /// <summary>"1.21" 과 "1.21.0" 처럼 같은 버전을 같은 문자열로 맞춘다.</summary>
    private static string Normalize(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        return parts.Length == 3 && parts[2] == "0" ? $"{parts[0]}.{parts[1]}" : gameVersion;
    }

    private static int[]? Numbers(string version)
    {
        var core = version.Split('-', 2)[0].Split('.');
        var numbers = new int[core.Length];
        for (var i = 0; i < core.Length; i++)
            if (!int.TryParse(core[i], out numbers[i]))
                return null;
        return numbers;
    }
}
