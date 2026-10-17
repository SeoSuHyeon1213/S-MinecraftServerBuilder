using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Builder.Core.Http;

namespace Builder.Core.Mods;

public enum ReleaseChannel
{
    Release = 0,
    Beta = 1,
    Alpha = 2,
}

/// <summary>모드 소스가 돌려주는 설치 가능한 파일 하나. 소스(Modrinth, CurseForge 등)에 관계없이 같은 형식이다.</summary>
public sealed record ModRelease(
    string Source,
    string ProjectId,
    string VersionId,
    string VersionNumber,
    ReleaseChannel Channel,
    string FileName,
    string? DownloadUrl,
    FileHash Hash,
    IReadOnlyList<string> RequiredDependencies,
    string PageUrl);

/// <param name="Release">조건에 맞는 파일. 없으면 null.</param>
/// <param name="OnlyUnstableAvailable">정식 버전은 없고 알파·베타만 있어서 찾지 못한 경우.</param>
public sealed record ModLookupResult(ModRelease? Release, bool OnlyUnstableAvailable);

/// <summary>
/// 모드 다운로드 사이트 공통 규격. 설치기는 이 규격만 사용하므로 소스를 추가해도 설치 로직은 바뀌지 않는다.
/// 현재 구현: Modrinth. 추후 확장: CurseForge (설계서 6장 참조).
/// </summary>
public interface IModSource
{
    /// <summary>모드 카탈로그의 sources 키와 builder.json 설치 기록에 쓰는 이름 (예: "modrinth").</summary>
    string Name { get; }

    /// <summary>
    /// 게임 버전·로더에 맞는 가장 적합한 파일을 찾는다. 정식 버전을 우선하고,
    /// <paramref name="allowUnstable"/>이 true일 때만 베타 → 알파 순으로 대신 고른다.
    /// </summary>
    Task<ModLookupResult> FindLatestAsync(
        string projectId, string gameVersion, string loader, bool allowUnstable, CancellationToken ct = default);
}

public sealed class ModrinthSource(HttpClient http) : IModSource
{
    public const string SourceName = "modrinth";
    private const string ApiBase = "https://api.modrinth.com/v2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public string Name => SourceName;

    public async Task<ModLookupResult> FindLatestAsync(
        string projectId, string gameVersion, string loader, bool allowUnstable, CancellationToken ct = default)
    {
        var url = $"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version" +
                  $"?loaders={Uri.EscapeDataString($"[\"{loader}\"]")}" +
                  $"&game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}";

        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Modrinth에 없는 프로젝트입니다: {projectId}");
        response.EnsureSuccessStatusCode();

        return SelectBest(await response.Content.ReadAsStringAsync(ct), allowUnstable);
    }

    /// <summary>Modrinth 버전 목록 JSON에서 설치할 파일을 고른다. 네트워크 없이 테스트할 수 있도록 분리했다.</summary>
    public static ModLookupResult SelectBest(string versionsJson, bool allowUnstable)
    {
        var versions = (JsonSerializer.Deserialize<List<VersionDto>>(versionsJson, Json) ?? [])
            .Where(v => v.Files is { Count: > 0 })
            .Select(v => (Dto: v, Channel: ParseChannel(v.VersionType)))
            .ToList();

        var pick = versions
            .Where(v => allowUnstable || v.Channel == ReleaseChannel.Release)
            .OrderBy(v => v.Channel)
            .ThenByDescending(v => v.Dto.DatePublished)
            .FirstOrDefault();

        if (pick.Dto is null)
            return new ModLookupResult(null, OnlyUnstableAvailable: versions.Count > 0);

        var v = pick.Dto;
        var file = v.Files!.FirstOrDefault(f => f.Primary) ?? v.Files![0];
        var hash = file.Hashes.TryGetValue("sha512", out var sha512)
            ? new FileHash("sha512", sha512)
            : new FileHash("sha1", file.Hashes["sha1"]);
        var dependencies = (v.Dependencies ?? [])
            .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
            .Select(d => d.ProjectId!)
            .Distinct()
            .ToList();

        return new ModLookupResult(
            new ModRelease(SourceName, v.ProjectId, v.Id, v.VersionNumber, pick.Channel, file.Filename, file.Url, hash,
                dependencies, $"https://modrinth.com/mod/{v.ProjectId}/version/{v.Id}"),
            OnlyUnstableAvailable: false);
    }

    private static ReleaseChannel ParseChannel(string? type) => type switch
    {
        "release" => ReleaseChannel.Release,
        "beta" => ReleaseChannel.Beta,
        _ => ReleaseChannel.Alpha,
    };

    private sealed record VersionDto(
        string Id, string ProjectId, string VersionNumber, string? VersionType, DateTimeOffset DatePublished,
        List<FileDto>? Files, List<DependencyDto>? Dependencies);

    private sealed record FileDto(Dictionary<string, string> Hashes, string Url, string Filename, bool Primary);

    private sealed record DependencyDto(string? ProjectId, string? DependencyType);
}

public static class FabricModMetadata
{
    /// <summary>jar 안의 fabric.mod.json에서 모드 ID를 읽는다. 출처와 관계없이 같은 모드인지 판별하는 기준이다.</summary>
    public static string? ReadModId(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry is null)
                return null;

            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
