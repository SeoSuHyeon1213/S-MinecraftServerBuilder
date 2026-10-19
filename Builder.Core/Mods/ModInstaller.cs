using System.Text.Json;
using Builder.Core.Http;
using Builder.Core.Loaders;
using Builder.Core.Server;

namespace Builder.Core.Mods;

/// <summary>Mods/catalog.json 한 항목. sources는 소스 이름 → 해당 사이트의 프로젝트 ID(또는 slug).</summary>
public sealed class ModCatalogEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public Dictionary<string, string> Sources { get; init; } = [];

    /// <summary>지원하는 모드 로더 (fabric, neoforge). 비어 있으면 모든 로더.</summary>
    public List<string> Loaders { get; init; } = [];

    public bool Supports(string loader) => Loaders.Count == 0 || Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);

    /// <summary>로더별로 프로젝트가 다른 모드는 "소스@로더" 키를 먼저 찾는다 (예: "modrinth@neoforge": "c2me-neoforge").</summary>
    public string? ProjectIdFor(string source, string loader) =>
        Sources.TryGetValue($"{source}@{loader}", out var specific) ? specific
        : Sources.TryGetValue(source, out var common) ? common
        : null;
}

public sealed class ModCatalog(IEnumerable<ModCatalogEntry> entries)
{
    private readonly List<ModCatalogEntry> entries = entries.ToList();

    public IReadOnlyList<ModCatalogEntry> All => entries;

    public static ModCatalog Load(string path) =>
        new(JsonSerializer.Deserialize<List<ModCatalogEntry>>(File.ReadAllText(path), BuilderHttp.Json)
            ?? throw new InvalidDataException($"모드 카탈로그를 읽을 수 없습니다: {path}"));

    public ModCatalogEntry Get(string id) =>
        entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"모드 카탈로그에 없는 모드: {id}");
}

/// <summary>builder.json에 남기는 설치 기록. 빌더가 설치한 파일만 업데이트·삭제 대상이 된다.</summary>
public sealed record InstalledMod
{
    /// <summary>카탈로그 모드 ID. 다른 모드의 필수 의존성으로만 설치된 경우 null.</summary>
    public string? CatalogId { get; init; }
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string ProjectId { get; init; }
    public required string VersionId { get; init; }
    public required string VersionNumber { get; init; }
    public required string FileName { get; init; }
    public required FileHash Hash { get; init; }
    /// <summary>jar의 모드 ID (Fabric 또는 NeoForge). 이름은 이전 builder.json과의 호환을 위해 유지한다.</summary>
    public string? FabricModId { get; init; }

    internal string Key => $"{Source}:{ProjectId}";
}

public sealed record ModChange(string Name, string Detail);

public sealed class ModSyncReport
{
    public List<ModChange> Installed { get; } = [];
    public List<ModChange> Updated { get; } = [];
    public List<ModChange> Kept { get; } = [];
    public List<ModChange> Removed { get; } = [];
    public List<ModChange> Skipped { get; } = [];
}

/// <summary>
/// 프리셋의 모드 목록을 서버 mods 폴더와 맞춘다.
/// - 맞는 버전이 없거나 다운로드에 실패한 모드는 건너뛰고 보고만 한다 (서버 생성은 계속).
/// - 사용자가 직접 넣은 jar는 건드리지 않으며, 같은 Fabric 모드 ID면 빌더 쪽 설치를 건너뛴다.
/// - 프리셋에서 빠진 모드는 지우지만, 의존성으로 설치한 모드는 사용자 모드가 쓸 수 있으므로 남긴다.
/// </summary>
public sealed class ModInstaller(HttpClient http, ModCatalog catalog, IReadOnlyList<IModSource> sources, Action<string> log)
{
    public const string ModsDirName = "mods";
    public const string FabricLoader = ModLoaders.Fabric;
    private const string StagingDirName = ".mchb-staging";

    /// <param name="update">false면 이미 설치된 모드는 조회 없이 유지하고, 없는 모드만 새로 찾는다. true면 모두 최신 호환 버전으로 다시 확인한다.</param>
    public async Task<ModSyncReport> SyncAsync(
        string serverDir, ServerInstanceConfig config, IReadOnlyList<string> modIds, bool update, CancellationToken ct = default)
    {
        var modsDir = Path.Combine(serverDir, ModsDirName);
        Directory.CreateDirectory(modsDir);
        var report = new ModSyncReport();

        // 파일이 사라진 기록은 설치되지 않은 것으로 본다.
        var managed = config.Mods.Where(m => File.Exists(Path.Combine(modsDir, m.FileName))).ToList();
        var managedFiles = managed.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var userModIds = Directory.EnumerateFiles(modsDir, "*.jar")
            .Where(f => !managedFiles.Contains(Path.GetFileName(f)))
            .SelectMany(f => ModMetadata.Read(f).ModIds)
            .ToHashSet(StringComparer.Ordinal);

        var wanted = modIds.Select(id => catalog.Get(id).Id).Distinct(StringComparer.Ordinal).ToList();
        var planned = new Dictionary<string, PlannedMod>(StringComparer.Ordinal);

        foreach (var id in wanted)
        {
            var entry = catalog.Get(id);
            if (!update && managed.Any(m => m.CatalogId == id))
                continue;

            var failure = await PlanEntryAsync(entry, config, managed, update, planned, ct);
            if (failure is not null)
                report.Skipped.Add(new(entry.Name, failure));
        }

        var result = new List<InstalledMod>();
        foreach (var mod in managed)
        {
            if (planned.ContainsKey(mod.Key))
                continue;
            if (mod.CatalogId is not null && !wanted.Contains(mod.CatalogId))
            {
                File.Delete(Path.Combine(modsDir, mod.FileName));
                report.Removed.Add(new(mod.Name, $"{mod.VersionNumber} (프리셋에서 제외됨)"));
                continue;
            }
            result.Add(mod);
            report.Kept.Add(new(mod.Name, mod.VersionNumber));
        }

        var stagingDir = Path.Combine(serverDir, StagingDirName);
        try
        {
            foreach (var plan in planned.Values)
            {
                var installed = await ApplyAsync(plan, managed, modsDir, stagingDir, managedFiles, userModIds, report, ct);
                if (installed is not null)
                    result.Add(installed);
            }
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }

        config.Mods.Clear();
        config.Mods.AddRange(result.OrderBy(m => m.CatalogId is null).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase));
        return report;
    }

    /// <summary>카탈로그 모드와 필수 의존성을 함께 찾는다. 의존성 하나라도 못 구하면 그 모드 전체를 건너뛴다.</summary>
    private async Task<string?> PlanEntryAsync(
        ModCatalogEntry entry, ServerInstanceConfig config, IReadOnlyList<InstalledMod> managed, bool update,
        Dictionary<string, PlannedMod> planned, CancellationToken ct)
    {
        if (!entry.Supports(config.Loader))
            return $"{ModLoaders.DisplayName(config.Loader)}용 버전이 없는 모드 (카탈로그 loaders: {string.Join(", ", entry.Loaders)})";

        string? lastFailure = null;
        foreach (var source in sources)
        {
            if (entry.ProjectIdFor(source.Name, config.Loader) is not { } projectId)
                continue;

            var (root, failure) = await LookupAsync(source, projectId, config, ct);
            if (root is null)
            {
                lastFailure = failure;
                continue;
            }

            var tree = new Dictionary<string, PlannedMod>(StringComparer.Ordinal)
            {
                [Key(root)] = new(root, entry.Id, entry.Name),
            };
            var queue = new Queue<ModRelease>([root]);
            while (queue.TryDequeue(out var current))
            {
                foreach (var dependencyId in current.RequiredDependencies)
                {
                    var key = $"{source.Name}:{dependencyId}";
                    if (tree.ContainsKey(key) || planned.ContainsKey(key))
                        continue;
                    if (!update && managed.Any(m => m.Key == key))
                        continue;

                    var (dependency, dependencyFailure) = await LookupAsync(source, dependencyId, config, ct);
                    if (dependency is null)
                        return $"필수 의존성 {dependencyId}: {dependencyFailure}";

                    tree[key] = new(dependency, null, Path.GetFileNameWithoutExtension(dependency.FileName));
                    queue.Enqueue(dependency);
                }
            }

            foreach (var (key, plan) in tree)
            {
                // 의존성으로 먼저 잡힌 모드가 카탈로그 모드이기도 하면 카탈로그 정보를 우선한다.
                if (!planned.TryGetValue(key, out var existing) || existing.CatalogId is null && plan.CatalogId is not null)
                    planned[key] = plan;
            }
            return null;
        }
        return lastFailure ?? "지원하는 모드 소스가 없음";
    }

    private async Task<(ModRelease? Release, string? Failure)> LookupAsync(
        IModSource source, string projectId, ServerInstanceConfig config, CancellationToken ct)
    {
        try
        {
            var result = await source.FindLatestAsync(projectId, config.MinecraftVersion, config.Loader, config.AllowUnstableMods, ct);
            if (result.Release is not null)
                return (result.Release, null);
            var target = $"Minecraft {config.MinecraftVersion} {ModLoaders.DisplayName(config.Loader)}";
            return (null, result.OnlyUnstableAvailable
                ? $"{target}용은 알파·베타 버전만 있음 (--allow-unstable-mods로 설치 가능)"
                : $"{target}용 버전이 아직 없음");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            return (null, $"{source.Name} 조회 실패: {ex.Message}");
        }
    }

    private async Task<InstalledMod?> ApplyAsync(
        PlannedMod plan, IReadOnlyList<InstalledMod> managed, string modsDir, string stagingDir,
        IReadOnlySet<string> managedFiles, IReadOnlySet<string> userModIds, ModSyncReport report, CancellationToken ct)
    {
        var release = plan.Release;
        var existing = managed.FirstOrDefault(m => m.Key == Key(release));
        if (existing is not null && existing.VersionId == release.VersionId)
        {
            report.Kept.Add(new(plan.CatalogId is null ? existing.Name : plan.Name, release.VersionNumber));
            return plan.CatalogId is null ? existing : existing with { CatalogId = plan.CatalogId, Name = plan.Name };
        }

        var fileName = Path.GetFileName(release.FileName);
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return Skip($"올바르지 않은 파일 이름: {release.FileName}");
        if (release.DownloadUrl is null)
            return Skip($"자동 다운로드 불가. 직접 받아 mods 폴더에 넣으세요: {release.PageUrl}");

        var target = Path.Combine(modsDir, fileName);
        if (File.Exists(target) && !managedFiles.Contains(fileName))
            return Skip($"같은 이름의 파일을 사용자가 이미 넣어 둠: {fileName}");

        var staged = Path.Combine(stagingDir, fileName);
        try
        {
            log($"모드 다운로드: {plan.Name} {release.VersionNumber}");
            await BuilderHttp.DownloadFileAsync(http, release.DownloadUrl, staged, expectedHash: release.Hash, ct: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
        {
            return Skip($"다운로드 실패: {ex.Message}");
        }

        var jarIds = ModMetadata.Read(staged).ModIds;
        var duplicate = jarIds.FirstOrDefault(userModIds.Contains);
        if (duplicate is not null)
            return Skip($"사용자가 직접 넣은 같은 모드({duplicate})가 있어 그것을 사용");
        var fabricId = jarIds.FirstOrDefault();

        // 의존성은 카탈로그 이름이 없으므로 jar의 모드 ID(예: fabric-api)를 이름으로 쓴다.
        var name = plan.CatalogId is null && fabricId is not null ? fabricId : plan.Name;

        if (existing is not null)
            File.Delete(Path.Combine(modsDir, existing.FileName));
        File.Move(staged, target, overwrite: true);

        if (existing is null)
            report.Installed.Add(new(name, Describe(release)));
        else
            report.Updated.Add(new(name, $"{existing.VersionNumber} → {Describe(release)}"));

        return new InstalledMod
        {
            CatalogId = plan.CatalogId,
            Name = name,
            Source = release.Source,
            ProjectId = release.ProjectId,
            VersionId = release.VersionId,
            VersionNumber = release.VersionNumber,
            FileName = fileName,
            Hash = release.Hash,
            FabricModId = fabricId,
        };

        InstalledMod? Skip(string reason)
        {
            report.Skipped.Add(new(plan.Name, reason));
            // 업데이트에 실패해도 기존 설치본은 계속 쓴다.
            return existing;
        }
    }

    private static string Describe(ModRelease release) =>
        release.Channel == ReleaseChannel.Release ? release.VersionNumber : $"{release.VersionNumber} ({release.Channel.ToString().ToLowerInvariant()})";

    private static string Key(ModRelease release) => $"{release.Source}:{release.ProjectId}";

    private sealed record PlannedMod(ModRelease Release, string? CatalogId, string Name);
}

/// <summary>
/// 로더를 바꿀 때 사용자가 직접 넣은 다른 로더 전용 jar를 mods-disabled/&lt;로더&gt;/ 로 옮겨 두고,
/// 다시 그 로더로 돌아오면 원래 자리로 되돌린다. 파일을 지우지 않으므로 언제든 복구할 수 있다.
/// </summary>
public static class UserModShelf
{
    public const string DisabledDirName = "mods-disabled";

    /// <returns>옮겨 둔 파일과 되돌린 파일 이름 목록.</returns>
    public static (List<string> Disabled, List<string> Restored) Swap(string serverDir, string fromLoader, string toLoader)
    {
        var modsDir = Path.Combine(serverDir, ModInstaller.ModsDirName);
        Directory.CreateDirectory(modsDir);
        var disabled = new List<string>();
        var restored = new List<string>();

        var shelf = Path.Combine(serverDir, DisabledDirName, fromLoader);
        foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar").ToList())
        {
            // 메타데이터가 없는 jar(라이브러리 등)와 두 로더를 모두 지원하는 jar는 그대로 둔다.
            if (!ModMetadata.Read(jar).IsForOtherLoaderThan(toLoader))
                continue;

            Directory.CreateDirectory(shelf);
            var name = Path.GetFileName(jar);
            var target = Path.Combine(shelf, name);
            if (File.Exists(target))
                target = Path.Combine(shelf, $"{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:yyyyMMddHHmmss}.jar");
            File.Move(jar, target);
            disabled.Add(name);
        }

        var back = Path.Combine(serverDir, DisabledDirName, toLoader);
        if (Directory.Exists(back))
        {
            foreach (var jar in Directory.EnumerateFiles(back, "*.jar").ToList())
            {
                var target = Path.Combine(modsDir, Path.GetFileName(jar));
                if (File.Exists(target))
                    continue;
                File.Move(jar, target);
                restored.Add(Path.GetFileName(jar));
            }
        }
        return (disabled, restored);
    }
}
