using System.Net.Http.Json;
using System.Text.Json;
using Builder.Core.Http;

namespace Builder.Core.Minecraft;

public sealed record FabricVersion(string Version, bool Stable);

/// <summary>https://meta.fabricmc.net 의 게임·로더·인스톨러 버전 조회와 서버 런처 jar 다운로드.</summary>
public sealed class FabricMetaClient(HttpClient http)
{
    private const string BaseUrl = "https://meta.fabricmc.net/v2/versions";

    public async Task<IReadOnlyList<FabricVersion>> GetGameVersionsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<FabricVersion>>($"{BaseUrl}/game", BuilderHttp.Json, ct) ?? [];

    /// <summary>해당 게임 버전을 지원하는 로더 목록. 지원하지 않는 버전이면 빈 목록.</summary>
    public async Task<IReadOnlyList<FabricVersion>> GetLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
    {
        var entries = await http.GetFromJsonAsync<List<LoaderEntry>>(
            $"{BaseUrl}/loader/{Uri.EscapeDataString(gameVersion)}", BuilderHttp.Json, ct) ?? [];
        return entries.Select(e => e.Loader).ToList();
    }

    public async Task<IReadOnlyList<FabricVersion>> GetInstallerVersionsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<FabricVersion>>($"{BaseUrl}/installer", BuilderHttp.Json, ct) ?? [];

    public static string ServerLauncherUrl(string game, string loader, string installer) =>
        $"{BaseUrl}/loader/{Uri.EscapeDataString(game)}/{Uri.EscapeDataString(loader)}/{Uri.EscapeDataString(installer)}/server/jar";

    public Task DownloadServerLauncherAsync(
        string game, string loader, string installer, string destinationPath,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        BuilderHttp.DownloadFileAsync(http, ServerLauncherUrl(game, loader, installer), destinationPath, progress, ct: ct);

    /// <summary>목록은 최신순이다. 가장 최신의 안정 버전, 없으면 가장 최신 버전.</summary>
    public static FabricVersion? PickLatestStable(IReadOnlyList<FabricVersion> versions) =>
        versions.FirstOrDefault(v => v.Stable) ?? versions.FirstOrDefault();

    private sealed record LoaderEntry(FabricVersion Loader);
}

/// <summary>Mojang 버전 메타데이터에서 게임 버전이 요구하는 Java 주 버전을 읽는다.</summary>
public sealed class MojangClient(HttpClient http)
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public async Task<int> GetRequiredJavaMajorAsync(string gameVersion, CancellationToken ct = default)
    {
        using var manifest = JsonDocument.Parse(await http.GetStringAsync(ManifestUrl, ct));
        foreach (var version in manifest.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (version.GetProperty("id").GetString() != gameVersion)
                continue;

            using var detail = JsonDocument.Parse(await http.GetStringAsync(version.GetProperty("url").GetString(), ct));
            return ReadJavaMajor(detail.RootElement);
        }
        throw new InvalidOperationException($"Mojang 버전 목록에 {gameVersion}이(가) 없습니다.");
    }

    /// <summary>javaVersion 항목이 없는 오래된 버전은 Java 8로 본다.</summary>
    public static int ReadJavaMajor(JsonElement versionDetail) =>
        versionDetail.TryGetProperty("javaVersion", out var java) && java.TryGetProperty("majorVersion", out var major)
            ? major.GetInt32()
            : 8;
}
