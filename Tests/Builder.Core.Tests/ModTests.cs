using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Builder.Core.Http;
using Builder.Core.Mods;
using Builder.Core.Presets;
using Builder.Core.Server;

namespace Builder.Core.Tests;

public class ModrinthSelectionTests
{
    private const string Versions = """
        [
          { "id": "alpha2", "project_id": "P1", "version_number": "2.0-alpha", "version_type": "alpha",
            "date_published": "2026-10-01T00:00:00Z",
            "files": [ { "hashes": { "sha512": "aa", "sha1": "a1" }, "url": "https://cdn/a2.jar", "filename": "a2.jar", "primary": true } ],
            "dependencies": [] },
          { "id": "rel1", "project_id": "P1", "version_number": "1.5", "version_type": "release",
            "date_published": "2026-09-01T00:00:00Z",
            "files": [
              { "hashes": { "sha512": "src", "sha1": "s1" }, "url": "https://cdn/sources.jar", "filename": "mod-sources.jar", "primary": false },
              { "hashes": { "sha512": "bb", "sha1": "b1" }, "url": "https://cdn/mod.jar", "filename": "mod-1.5.jar", "primary": true } ],
            "dependencies": [
              { "project_id": "FAPI", "dependency_type": "required" },
              { "project_id": "OPT", "dependency_type": "optional" },
              { "project_id": "BAD", "dependency_type": "incompatible" } ] },
          { "id": "rel0", "project_id": "P1", "version_number": "1.0", "version_type": "release",
            "date_published": "2026-08-01T00:00:00Z",
            "files": [ { "hashes": { "sha512": "cc" }, "url": "https://cdn/old.jar", "filename": "old.jar", "primary": true } ],
            "dependencies": [] }
        ]
        """;

    [Fact]
    public void PrefersNewestReleaseOverNewerAlpha()
    {
        var release = ModrinthSource.SelectBest(Versions, ReleaseChannel.Alpha).Release!;
        Assert.Equal("rel1", release.VersionId);
        Assert.Equal("mod-1.5.jar", release.FileName);
        Assert.Equal(new FileHash("sha512", "bb"), release.Hash);
        Assert.Equal(["FAPI"], release.RequiredDependencies);
        Assert.Equal(ReleaseChannel.Release, release.Channel);
    }

    [Fact]
    public void ReportsUnstableOnlyUnlessAllowed()
    {
        const string alphaOnly = """
            [ { "id": "a", "project_id": "P", "version_number": "0.1", "version_type": "alpha", "date_published": "2026-10-01T00:00:00Z",
                "files": [ { "hashes": { "sha512": "x" }, "url": "https://cdn/a.jar", "filename": "a.jar", "primary": true } ] } ]
            """;
        var denied = ModrinthSource.SelectBest(alphaOnly, ReleaseChannel.Release);
        Assert.Null(denied.Release);
        Assert.True(denied.OnlyUnstableAvailable);

        Assert.Equal(ReleaseChannel.Alpha, ModrinthSource.SelectBest(alphaOnly, ReleaseChannel.Alpha).Release!.Channel);

        var none = ModrinthSource.SelectBest("[]", ReleaseChannel.Release);
        Assert.Null(none.Release);
        Assert.False(none.OnlyUnstableAvailable);
    }

    [Fact]
    public void BetaLimitTakesBetaButNotAlpha()
    {
        const string unstable = """
            [ { "id": "a", "project_id": "P", "version_number": "3.0-a", "version_type": "alpha", "date_published": "2026-10-03T00:00:00Z",
                "files": [ { "hashes": { "sha512": "x" }, "url": "https://cdn/a.jar", "filename": "a.jar", "primary": true } ] },
              { "id": "b", "project_id": "P", "version_number": "2.12-b", "version_type": "beta", "date_published": "2026-10-01T00:00:00Z",
                "files": [ { "hashes": { "sha512": "y" }, "url": "https://cdn/b.jar", "filename": "b.jar", "primary": true } ] } ]
            """;
        Assert.Equal("b", ModrinthSource.SelectBest(unstable, ReleaseChannel.Beta).Release!.VersionId);
        Assert.Equal("b", ModrinthSource.SelectBest(unstable, ReleaseChannel.Alpha).Release!.VersionId); // 같은 조건이면 덜 불안정한 쪽

        var alphaOnly = ModrinthSource.SelectBest(unstable.Replace("\"beta\"", "\"alpha\""), ReleaseChannel.Beta);
        Assert.Null(alphaOnly.Release);
        Assert.True(alphaOnly.OnlyUnstableAvailable);
    }
}

public class ModInstallerTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mchb-mods-").FullName;
    private readonly FakeSource source = new();
    private readonly FakeHandler handler = new();
    private readonly List<string> logs = [];

    private static readonly ModCatalog Catalog = new([
        new ModCatalogEntry { Id = "lithium", Name = "Lithium", Sources = { ["fake"] = "lithium" } },
        new ModCatalogEntry { Id = "chunky", Name = "Chunky", Sources = { ["fake"] = "chunky" } },
        new ModCatalogEntry { Id = "c2me", Name = "C2ME", Sources = { ["fake"] = "c2me" } },
        new ModCatalogEntry { Id = "modernfix", Name = "ModernFix", Sources = { ["fake"] = "modernfix" } },
        new ModCatalogEntry { Id = "geyser", Name = "Geyser", Sources = { ["fake"] = "geyser" }, AllowBeta = true },
        new ModCatalogEntry { Id = "floodgate", Name = "Floodgate", Sources = { ["fake"] = "floodgate" } },
    ]);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private ModsDir Mods => new(Path.Combine(dir, ModInstaller.ModsDirName));

    private ModInstaller Installer() => new(new HttpClient(handler), Catalog, [source], logs.Add);

    private static ServerInstanceConfig Config(bool allowUnstable = false) => new()
    {
        MinecraftVersion = "26.3", LoaderVersion = "0.19.5", InstallerVersion = "1.1.2", AllowUnstableMods = allowUnstable,
    };

    /// <summary>프로젝트 하나를 가짜 소스와 가짜 CDN에 등록한다.</summary>
    private void Publish(string projectId, string version, string fabricId, ReleaseChannel channel = ReleaseChannel.Release,
        string[]? deps = null, bool corruptHash = false)
    {
        var bytes = Jar(fabricId, version);
        var url = $"https://cdn.test/{projectId}/{version}.jar";
        handler.Files[url] = bytes;
        var hash = corruptHash ? "00" : Convert.ToHexStringLower(SHA512.HashData(bytes));
        source.Releases[projectId] = new ModRelease("fake", projectId, $"{projectId}-{version}", version, channel,
            $"{projectId}-{version}.jar", url, new FileHash("sha512", hash), deps ?? [], $"https://test/{projectId}");
    }

    [Fact]
    public async Task InstallsModsWithRequiredDependencies()
    {
        Publish("lithium", "1.0", "lithium");
        Publish("chunky", "1.5", "chunky", deps: ["fabric-api"]);
        Publish("fabric-api", "0.163", "fabric-api");
        var config = Config();

        var report = await Installer().SyncAsync(dir, config, ["lithium", "chunky"], update: true);

        Assert.Equal(3, report.Installed.Count);
        Assert.Empty(report.Skipped);
        Assert.Equal(["chunky-1.5.jar", "fabric-api-0.163.jar", "lithium-1.0.jar"], Mods.Jars);
        var dependency = Assert.Single(config.Mods, m => m.ProjectId == "fabric-api");
        Assert.Null(dependency.CatalogId);
        Assert.Equal("chunky", Assert.Single(config.Mods, m => m.ProjectId == "chunky").FabricModId);
        Assert.False(Directory.Exists(Path.Combine(dir, ".mchb-staging")));
    }

    [Fact]
    public async Task SkipsMissingUnstableAndBrokenDependencyMods()
    {
        Publish("lithium", "1.0", "lithium");
        Publish("c2me", "0.4", "c2me", ReleaseChannel.Alpha);
        Publish("chunky", "1.5", "chunky", deps: ["fabric-api"]); // fabric-api 미등록
        var config = Config();

        var report = await Installer().SyncAsync(dir, config, ["lithium", "c2me", "chunky", "modernfix"], update: true);

        Assert.Equal(["lithium-1.0.jar"], Mods.Jars);
        Assert.Contains(report.Skipped, s => s.Name == "C2ME" && s.Detail.Contains("알파·베타"));
        Assert.Contains(report.Skipped, s => s.Name == "Chunky" && s.Detail.Contains("필수 의존성 fabric-api"));
        Assert.Contains(report.Skipped, s => s.Name == "ModernFix" && s.Detail.Contains("버전이 아직 없음"));
    }

    [Fact]
    public async Task InstallsUnstableWhenAllowed()
    {
        Publish("c2me", "0.4", "c2me", ReleaseChannel.Alpha);

        var report = await Installer().SyncAsync(dir, Config(allowUnstable: true), ["c2me"], update: true);

        Assert.Contains("(alpha)", Assert.Single(report.Installed).Detail);
    }

    [Fact]
    public async Task CatalogBetaAllowanceAppliesOnlyToThatMod()
    {
        Publish("geyser", "2.12.1-b1273", "geyser", ReleaseChannel.Beta, deps: ["fabric-api"]);
        Publish("fabric-api", "0.200-beta", "fabric-api", ReleaseChannel.Beta);
        Publish("floodgate", "2.2.7-b69", "floodgate");
        Publish("chunky", "2.0-beta", "chunky", ReleaseChannel.Beta);
        var config = Config();

        var report = await Installer().SyncAsync(dir, config, ["geyser", "floodgate", "chunky"], update: true);

        // Geyser 자체는 베타를 허용하지만, 베타뿐인 의존성은 서버 설정(정식만)을 따라 건너뛴다.
        Assert.Contains(report.Skipped, s => s.Name == "Geyser" && s.Detail.Contains("필수 의존성 fabric-api"));
        Assert.Contains(report.Skipped, s => s.Name == "Chunky" && s.Detail.Contains("알파·베타"));
        Assert.Equal(["floodgate-2.2.7-b69.jar"], Mods.Jars);

        Publish("fabric-api", "0.200", "fabric-api");
        report = await Installer().SyncAsync(dir, config, ["geyser", "floodgate", "chunky"], update: true);

        Assert.Contains(report.Installed, i => i.Name == "Geyser" && i.Detail.Contains("(beta)"));
        Assert.Contains("geyser-2.12.1-b1273.jar", Mods.Jars);
        Assert.Equal(ReleaseChannel.Beta, source.MaxChannels["geyser"]);
        Assert.Equal(ReleaseChannel.Release, source.MaxChannels["fabric-api"]);
        Assert.Equal(ReleaseChannel.Release, source.MaxChannels["chunky"]);
    }

    [Fact]
    public async Task TurningCrossplayOffRemovesItsModsButKeepsDependencies()
    {
        Publish("lithium", "1.0", "lithium");
        Publish("geyser", "2.12", "geyser", ReleaseChannel.Beta, deps: ["fabric-api"]);
        Publish("fabric-api", "0.200", "fabric-api");
        Publish("floodgate", "2.2.7", "floodgate");
        var config = Config();
        config.Crossplay.Enabled = true;
        await Installer().SyncAsync(dir, config, Crossplay.WantedMods(["lithium"], config), update: true);
        Assert.Equal(["fabric-api-0.200.jar", "floodgate-2.2.7.jar", "geyser-2.12.jar", "lithium-1.0.jar"], Mods.Jars);

        config.Crossplay.Enabled = false;
        var report = await Installer().SyncAsync(dir, config, Crossplay.WantedMods(["lithium"], config), update: false);

        Assert.Equal(["Floodgate", "Geyser"], report.Removed.Select(r => r.Name).Order());
        Assert.Equal(["fabric-api-0.200.jar", "lithium-1.0.jar"], Mods.Jars);
    }

    [Fact]
    public async Task NeverOverridesUserInstalledModWithSameFabricId()
    {
        Directory.CreateDirectory(Mods.Path);
        File.WriteAllBytes(Path.Combine(Mods.Path, "my-lithium-build.jar"), Jar("lithium", "custom"));
        Publish("lithium", "1.0", "lithium");
        var config = Config();

        var report = await Installer().SyncAsync(dir, config, ["lithium"], update: true);

        Assert.Equal(["my-lithium-build.jar"], Mods.Jars);
        Assert.Contains("직접 넣은", Assert.Single(report.Skipped).Detail);
        Assert.Empty(config.Mods);
    }

    [Fact]
    public async Task RejectsFileWithWrongHash()
    {
        Publish("lithium", "1.0", "lithium", corruptHash: true);

        var report = await Installer().SyncAsync(dir, Config(), ["lithium"], update: true);

        Assert.Empty(Mods.Jars);
        Assert.Contains("체크섬", Assert.Single(report.Skipped).Detail);
    }

    [Fact]
    public async Task PresetChangeRemovesDroppedModButKeepsDependencies()
    {
        Publish("c2me", "0.4", "c2me");
        Publish("chunky", "1.5", "chunky", deps: ["fabric-api"]);
        Publish("fabric-api", "0.163", "fabric-api");
        var config = Config();
        await Installer().SyncAsync(dir, config, ["c2me", "chunky"], update: true);

        var report = await Installer().SyncAsync(dir, config, ["lithium"], update: false);

        Assert.Equal(["C2ME", "Chunky"], report.Removed.Select(r => r.Name).Order());
        Assert.Contains("fabric-api-0.163.jar", Mods.Jars);
        Assert.DoesNotContain("c2me-0.4.jar", Mods.Jars);
        Assert.Contains(report.Skipped, s => s.Name == "Lithium"); // 소스에 없음
    }

    [Fact]
    public async Task KeepsInstalledModsWithoutLookupUnlessUpdating()
    {
        Publish("lithium", "1.0", "lithium");
        var config = Config();
        await Installer().SyncAsync(dir, config, ["lithium"], update: true);
        source.Lookups.Clear();

        Publish("lithium", "1.1", "lithium");
        var keep = await Installer().SyncAsync(dir, config, ["lithium"], update: false);
        Assert.Empty(source.Lookups);
        Assert.Single(keep.Kept);
        Assert.Equal(["lithium-1.0.jar"], Mods.Jars);

        var update = await Installer().SyncAsync(dir, config, ["lithium"], update: true);
        Assert.Equal("1.0 → 1.1", Assert.Single(update.Updated).Detail);
        Assert.Equal(["lithium-1.1.jar"], Mods.Jars);
        Assert.Equal("1.1", Assert.Single(config.Mods).VersionNumber);
    }

    [Fact]
    public async Task KeepsExistingVersionWhenUpdateDownloadFails()
    {
        Publish("lithium", "1.0", "lithium");
        var config = Config();
        await Installer().SyncAsync(dir, config, ["lithium"], update: true);

        Publish("lithium", "1.1", "lithium", corruptHash: true);
        var report = await Installer().SyncAsync(dir, config, ["lithium"], update: true);

        Assert.Single(report.Skipped);
        Assert.Equal(["lithium-1.0.jar"], Mods.Jars);
        Assert.Equal("1.0", Assert.Single(config.Mods).VersionNumber);
    }

    [Fact]
    public void PresetsReferenceOnlyCatalogMods()
    {
        var mods = ModCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Mods", "catalog.json"));
        var presets = PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));
        foreach (var preset in presets.All)
            foreach (var id in preset.Mods)
                Assert.True(mods.All.Any(m => m.Id == id), $"{preset.Id}: {id}");

        // 설계서 4장: C2ME는 일반·중간 단계만
        Assert.Contains("c2me", presets.Get("normal").Mods);
        Assert.Contains("c2me", presets.Get("medium").Mods);
        Assert.DoesNotContain("c2me", presets.Get("low").Mods);
        Assert.DoesNotContain("c2me", presets.Get("minimum").Mods);
        Assert.All(mods.All, m => Assert.True(m.Sources.ContainsKey(ModrinthSource.SourceName)));

        // 크로스플레이 모드는 프리셋 묶음이 아니라 crossplay 설정으로만 설치한다. Geyser는 베타로만 배포된다 (2026-10-27 확인).
        foreach (var id in Crossplay.ModIds)
        {
            Assert.Contains(mods.All, m => m.Id == id);
            Assert.All(presets.All, p => Assert.DoesNotContain(id, p.Mods));
        }
        Assert.True(mods.Get("geyser").AllowBeta);
        Assert.False(mods.Get("floodgate").AllowBeta);
    }

    private static byte[] Jar(string fabricId, string version)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open());
            writer.Write($$"""{ "schemaVersion": 1, "id": "{{fabricId}}", "version": "{{version}}" }""");
        }
        return stream.ToArray();
    }

    private sealed record ModsDir(string Path)
    {
        public List<string> Jars => Directory.Exists(Path)
            ? Directory.EnumerateFiles(Path, "*.jar").Select(f => System.IO.Path.GetFileName(f)).Order().ToList()
            : [];
    }

    /// <summary>프로젝트마다 버전 하나만 있는 가짜 소스. 그 버전의 채널이 허용 범위를 넘으면 "불안정 버전만 있음"으로 답한다.</summary>
    private sealed class FakeSource : IModSource
    {
        public Dictionary<string, ModRelease> Releases { get; } = [];
        public List<string> Lookups { get; } = [];
        public Dictionary<string, ReleaseChannel> MaxChannels { get; } = [];
        public string Name => "fake";

        public Task<ModLookupResult> FindLatestAsync(string projectId, string gameVersion, string loader, ReleaseChannel maxChannel, CancellationToken ct)
        {
            Lookups.Add(projectId);
            MaxChannels[projectId] = maxChannel;
            if (!Releases.TryGetValue(projectId, out var release))
                return Task.FromResult(new ModLookupResult(null, false));
            if (release.Channel > maxChannel)
                return Task.FromResult(new ModLookupResult(null, true));
            return Task.FromResult(new ModLookupResult(release, false));
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
