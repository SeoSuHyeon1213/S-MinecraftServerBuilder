using System.IO.Compression;
using Builder.Core.Loaders;
using Builder.Core.Mods;
using Builder.Core.Presets;

namespace Builder.Core.Tests;

public class NeoForgeVersionTests
{
    private const string Metadata = """
        <metadata><versioning><versions>
          <version>20.4.253</version>
          <version>21.0.167</version>
          <version>21.1.258</version>
          <version>21.1.262</version>
          <version>21.10.5</version>
          <version>26.1.2.116</version>
          <version>26.1.2.122</version>
          <version>26.3.0.75-beta</version>
          <version>26.3.0.90</version>
          <version>26.4.0.3-beta</version>
        </versions></versioning></metadata>
        """;

    private static readonly IReadOnlyList<string> All = NeoForgeVersions.ParseMetadata(Metadata);

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.21.0", "21.0.")]
    [InlineData("26.3", "26.3.0.")]
    [InlineData("26.1.2", "26.1.2.")]
    [InlineData("26.3-snapshot-1", null)]
    [InlineData("26w14a", null)]
    public void MapsGameVersionToPrefix(string game, string? prefix) =>
        Assert.Equal(prefix, NeoForgeVersions.Prefix(game));

    [Theory]
    [InlineData("21.1.262", "1.21.1")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("20.4.253", "1.20.4")]
    [InlineData("26.3.0.90", "26.3")]
    [InlineData("26.1.2.116", "26.1.2")]
    [InlineData("26.3.0.75-beta", "26.3")]
    [InlineData("26.3.0", null)]
    public void MapsNeoForgeVersionToGame(string neoForge, string? game) =>
        Assert.Equal(game, NeoForgeVersions.GameVersionOf(neoForge));

    [Fact]
    public void ForGameReturnsNewestFirstWithoutPrefixCollisions()
    {
        Assert.Equal(["21.1.262", "21.1.258"], NeoForgeVersions.ForGame(All, "1.21.1"));
        Assert.Equal(["26.3.0.90", "26.3.0.75-beta"], NeoForgeVersions.ForGame(All, "26.3"));
        Assert.Equal(["21.0.167"], NeoForgeVersions.ForGame(All, "1.21"));
        Assert.Empty(NeoForgeVersions.ForGame(All, "1.19.2"));
    }

    [Fact]
    public void SummaryPrefersStableBuildsAndNewestGame()
    {
        var summary = NeoForgeVersions.Summarize(All);
        Assert.Equal(("26.4", "26.4.0.3-beta", false), summary[0]);
        Assert.Equal(("26.3", "26.3.0.90", true), summary[1]);
        Assert.Equal("26.3", summary.First(s => s.Stable).Game);
        Assert.Contains(("1.21.10", "21.10.5", true), summary);
    }

    [Fact]
    public void StableSortsAboveBetaOfSameNumber() =>
        Assert.True(NeoForgeVersions.Comparer.Compare("26.3.0.90", "26.3.0.90-beta") > 0);

    [Theory]
    [InlineData("fabric", ModLoaders.Fabric)]
    [InlineData("네오포지", ModLoaders.NeoForge)]
    [InlineData("NeoForge", ModLoaders.NeoForge)]
    [InlineData("페브릭", ModLoaders.Fabric)]
    public void NormalizesLoaderNames(string input, string expected) =>
        Assert.Equal(expected, ModLoaders.Normalize(input));

    [Fact]
    public void RejectsUnknownLoader() =>
        Assert.Throws<ArgumentException>(() => ModLoaders.Normalize("forge"));

    [Fact]
    public void NeoForgeLaunchUsesArgumentFile()
    {
        var loader = new NeoForgeServerLoader(new HttpClient(), _ => { });
        Assert.Equal(["@libraries/net/neoforged/neoforge/21.1.262/win_args.txt", "nogui"], loader.LaunchArguments("21.1.262", windows: true));
        Assert.Equal(["@libraries/net/neoforged/neoforge/21.1.262/unix_args.txt", "nogui"], loader.LaunchArguments("21.1.262", windows: false));
    }
}

public class ModMetadataTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mchb-meta-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void ReadsNeoForgeModIdsButNotDependencies()
    {
        var jar = WriteJar("sodium.jar", neoToml: """
            modLoader="javafml"
            [[mods]] # 주 모드
            modId="sodium"
            version="1.0"
            [[mods]]
            modId = 'sodium_extra'
            [[dependencies.sodium]]
            modId="neoforge"
            [[dependencies.sodium]]
            modId="minecraft"
            """);

        var info = ModMetadata.Read(jar);
        Assert.Equal([ModLoaders.NeoForge], info.Loaders);
        Assert.Equal(["sodium", "sodium_extra"], info.ModIds);
        Assert.True(info.IsForOtherLoaderThan(ModLoaders.Fabric));
    }

    [Fact]
    public void MultiLoaderJarBelongsToBoth()
    {
        var info = ModMetadata.Read(WriteJar("both.jar", fabricId: "both", neoToml: "[[mods]]\nmodId=\"both\"\n"));
        Assert.Equal(["both"], info.ModIds);
        Assert.False(info.IsForOtherLoaderThan(ModLoaders.Fabric));
        Assert.False(info.IsForOtherLoaderThan(ModLoaders.NeoForge));
    }

    [Fact]
    public void ShelfMovesOtherLoaderJarsAndRestoresThem()
    {
        var mods = Directory.CreateDirectory(Path.Combine(dir, ModInstaller.ModsDirName)).FullName;
        WriteJar(Path.Combine(mods, "fabric-only.jar"), fabricId: "fabriconly");
        WriteJar(Path.Combine(mods, "both.jar"), fabricId: "both", neoToml: "[[mods]]\nmodId=\"both\"\n");
        File.WriteAllBytes(Path.Combine(mods, "library.jar"), EmptyJar());

        var (disabled, restored) = UserModShelf.Swap(dir, ModLoaders.Fabric, ModLoaders.NeoForge);
        Assert.Equal(["fabric-only.jar"], disabled);
        Assert.Empty(restored);
        Assert.True(File.Exists(Path.Combine(dir, UserModShelf.DisabledDirName, "fabric", "fabric-only.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "both.jar")));
        Assert.True(File.Exists(Path.Combine(mods, "library.jar")));

        WriteJar(Path.Combine(mods, "neo-only.jar"), neoToml: "[[mods]]\nmodId=\"neoonly\"\n");
        (disabled, restored) = UserModShelf.Swap(dir, ModLoaders.NeoForge, ModLoaders.Fabric);
        Assert.Equal(["neo-only.jar"], disabled);
        Assert.Equal(["fabric-only.jar"], restored);
        Assert.True(File.Exists(Path.Combine(mods, "fabric-only.jar")));
    }

    private string WriteJar(string path, string? fabricId = null, string? neoToml = null)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(dir, path);
        using var zip = ZipFile.Open(full, ZipArchiveMode.Create);
        if (fabricId is not null)
            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                writer.Write($$"""{ "schemaVersion": 1, "id": "{{fabricId}}" }""");
        if (neoToml is not null)
            using (var writer = new StreamWriter(zip.CreateEntry("META-INF/neoforge.mods.toml").Open()))
                writer.Write(neoToml);
        return full;
    }

    private static byte[] EmptyJar()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("META-INF/MANIFEST.MF");
        return stream.ToArray();
    }
}

public class LoaderCatalogTests
{
    [Fact]
    public void CatalogEntriesResolvePerLoaderProjects()
    {
        var mods = ModCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Mods", "catalog.json"));

        var c2me = mods.Get("c2me");
        Assert.Equal("c2me-fabric", c2me.ProjectIdFor(ModrinthSource.SourceName, ModLoaders.Fabric));
        Assert.Equal("c2me-neoforge", c2me.ProjectIdFor(ModrinthSource.SourceName, ModLoaders.NeoForge));

        var krypton = mods.Get("krypton");
        Assert.True(krypton.Supports(ModLoaders.Fabric));
        Assert.False(krypton.Supports(ModLoaders.NeoForge));

        Assert.True(mods.Get("lithium").Supports(ModLoaders.NeoForge));
        Assert.Null(new ModCatalogEntry { Id = "x", Name = "X" }.ProjectIdFor("modrinth", ModLoaders.Fabric));
    }

    [Theory]
    [InlineData(ModLoaders.Fabric, 0, 2048)]
    [InlineData(ModLoaders.Fabric, 50, 5376)]
    [InlineData(ModLoaders.NeoForge, 50, 5888)]
    public void HeapAdviceGrowsWithModCount(string loader, int mods, int expected) =>
        Assert.Equal(expected, ModdedHeapAdvisor.RecommendedHeapMb(loader, mods));
}
