using System.Text.Json;
using Builder.Core.Hardware;
using Builder.Core.Java;
using Builder.Core.Minecraft;
using Builder.Core.Presets;
using Builder.Core.Server;

namespace Builder.Core.Tests;

public class JavaVersionParserTests
{
    [Theory]
    [InlineData("java version \"1.8.0_451\"\nJava(TM) SE Runtime Environment", 8)]
    [InlineData("openjdk version \"25.0.4\" 2026-07-21 LTS", 25)]
    [InlineData("openjdk version \"21\" 2023-09-19", 21)]
    [InlineData("openjdk version \"17-ea\" 2021-09-14", 17)]
    public void ParsesMajorVersion(string output, int expected) =>
        Assert.Equal(expected, JavaVersionParser.ParseMajorFromOutput(output));

    [Fact]
    public void ReturnsNullForUnrecognizedOutput() =>
        Assert.Null(JavaVersionParser.ParseMajorFromOutput("command not found"));

    [Fact]
    public void SelectsLowestCompatibleVersionPreferringManaged()
    {
        var all = new[]
        {
            new JavaInstallation("path8", 8, "", false),
            new JavaInstallation("system25", 25, "", false),
            new JavaInstallation("managed25", 25, "", true),
            new JavaInstallation("path26", 26, "", false),
        };
        Assert.Equal("managed25", JavaLocator.SelectFor(all, 25)!.ExecutablePath);
        Assert.Equal("managed25", JavaLocator.SelectFor(all, 21)!.ExecutablePath);
        Assert.Null(JavaLocator.SelectFor(all, 27));
    }
}

public class PresetTests
{
    private static PresetCatalog Catalog() =>
        PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));

    // 설계서 v0.2 4장 표의 값과 일치해야 한다.
    [Theory]
    [InlineData("normal", 6144, "10", "8", "100", "true", "256")]
    [InlineData("medium", 5120, "8", "6", "85", "true", "256")]
    [InlineData("low", 4096, "6", "5", "70", "false", "512")]
    [InlineData("minimum", 3072, "4", "3", "50", "false", "512")]
    public void PresetValuesMatchDesign(string id, int heap, string view, string sim, string entity, string syncWrites, string compression)
    {
        var p = Catalog().Get(id);
        Assert.Equal(heap, p.HeapMb);
        Assert.Equal(view, p.ServerProperties["view-distance"]);
        Assert.Equal(sim, p.ServerProperties["simulation-distance"]);
        Assert.Equal(entity, p.ServerProperties["entity-broadcast-range-percentage"]);
        Assert.Equal(syncWrites, p.ServerProperties["sync-chunk-writes"]);
        Assert.Equal(compression, p.ServerProperties["network-compression-threshold"]);
        Assert.Contains("-XX:+UseG1GC", p.JvmFlags);
        // 안전 장치: 비동기 청크 저장 단계는 자동 백업 필수
        Assert.Equal(syncWrites == "false", p.RequiresAutoBackup);
    }

    [Fact]
    public void CatalogIsOrderedAndFindsKoreanNames()
    {
        var catalog = Catalog();
        Assert.Equal(["normal", "medium", "low", "minimum"], catalog.All.Select(p => p.Id));
        Assert.Equal("low", catalog.Get("저").Id);
        Assert.Equal("minimum", catalog.Get("MINIMUM").Id);
        Assert.Throws<ArgumentException>(() => catalog.Get("ultra"));
    }

    [Fact]
    public void OverridesAreMergedOverPresetDefaults()
    {
        var resolved = PresetResolver.Resolve(Catalog().Get("low"), new Dictionary<string, string>
        {
            ["server.view-distance"] = "8",
            ["server.motd"] = "hello",
            ["jvm.heapMb"] = "3584",
        });

        Assert.Equal("8", resolved.ServerProperties["view-distance"]);
        Assert.Equal("hello", resolved.ServerProperties["motd"]);
        Assert.Equal("5", resolved.ServerProperties["simulation-distance"]);
        Assert.Equal(3584, resolved.HeapMb);
    }

    [Theory]
    [InlineData("view-distance", "8")]
    [InlineData("server.", "8")]
    [InlineData("jvm.heapMb", "abc")]
    [InlineData("jvm.heapMb", "512")]
    public void InvalidOverridesAreRejected(string key, string value) =>
        Assert.Throws<ArgumentException>(() =>
            PresetResolver.Resolve(Catalog().Get("low"), new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void LaunchArgumentsUseEqualMinAndMaxHeap()
    {
        var args = ServerLaunch.BuildArguments(PresetResolver.Resolve(Catalog().Get("minimum"), null));
        Assert.Equal("-Xms3072M", args[0]);
        Assert.Equal("-Xmx3072M", args[1]);
        Assert.Equal(["-jar", ServerLaunch.LauncherJar, "nogui"], args.TakeLast(3));
    }

    private const long Gb = 1024L * 1024 * 1024;

    // 추천은 전체 RAM - OS 몫(여기서는 3GB) 기준이며, 측정 순간의 여유 RAM(1GB)과 무관해야 한다.
    [Theory]
    [InlineData(12, 11.8, "normal")]
    [InlineData(4, 11.8, "medium")]
    [InlineData(6, 10.0, "medium")]
    [InlineData(2, 10.0, "low")]
    [InlineData(6, 9.0, "low")]
    [InlineData(12, 8.0, "minimum")]
    [InlineData(12, 4.0, "minimum")]
    public void RecommendsPresetByTotalMemoryAndCores(int cores, double totalGb, string expected)
    {
        var hw = new HardwareInfo(cores, (long)(totalGb * Gb), 1 * Gb, "test");
        Assert.Equal(expected, PresetRecommender.Recommend(hw, osReserveGb: 3).PresetId);
    }

    [Fact]
    public void ReportsShortfallFromCurrentAvailableMemory()
    {
        // 12GB PC에서 브라우저 등으로 여유 RAM이 2.4GB뿐인 경우
        var busy = PresetRecommender.Recommend(new HardwareInfo(12, (long)(11.8 * Gb), (long)(2.4 * Gb), "test"), osReserveGb: 3);
        Assert.Equal("normal", busy.PresetId);
        Assert.Equal(5.6, busy.ShortfallGb, precision: 1);
        Assert.False(busy.BelowMinimum);

        var idle = PresetRecommender.Recommend(new HardwareInfo(12, (long)(11.8 * Gb), 9 * Gb, "test"), osReserveGb: 3);
        Assert.Equal(0, idle.ShortfallGb);
    }

    [Fact]
    public void FlagsMachinesBelowMinimumTier()
    {
        Assert.True(PresetRecommender.Recommend(new HardwareInfo(4, 6 * Gb, 2 * Gb, "test"), osReserveGb: 3).BelowMinimum);
        Assert.False(PresetRecommender.Recommend(new HardwareInfo(4, 8 * Gb, 2 * Gb, "test"), osReserveGb: 3).BelowMinimum);
    }

    [Fact]
    public void RequiredMemoryForHeapIncludesJvmOverhead() =>
        Assert.Equal(4.5, PresetRecommender.RequiredGbForHeap(3072));
}

public class ServerFileTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mchb-test-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void PropertiesMergePreservesCommentsAndOtherKeys()
    {
        var file = new ServerPropertiesFile([
            "#Minecraft server properties",
            "motd=My Server",
            "view-distance=10",
            "level-seed=",
        ]);
        file.Set("view-distance", "4");
        file.Set("sync-chunk-writes", "false");

        Assert.Equal([
            "#Minecraft server properties",
            "motd=My Server",
            "view-distance=4",
            "level-seed=",
            "sync-chunk-writes=false",
        ], file.Lines);
        Assert.Equal("My Server", file.Get("motd"));
        Assert.Equal("", file.Get("level-seed"));
        Assert.Null(file.Get("missing"));
    }

    [Fact]
    public void InstanceConfigRoundTripsAndAcceptsNumericOverrides()
    {
        File.WriteAllText(Path.Combine(dir, ServerInstanceConfig.FileName), """
            {
              "minecraftVersion": "26.3",
              "loaderVersion": "0.19.5",
              "installerVersion": "1.1.2",
              "javaMajorVersion": 25,
              "preset": "low",
              "overrides": { "server.view-distance": 8, "server.pvp": false }
            }
            """);

        var config = ServerInstanceConfig.Load(dir);
        Assert.Equal("8", config.Overrides["server.view-distance"]);
        Assert.Equal("false", config.Overrides["server.pvp"]);

        config.Preset = "minimum";
        config.Save(dir);
        var reloaded = ServerInstanceConfig.Load(dir);
        Assert.Equal("minimum", reloaded.Preset);
        Assert.Equal(25, reloaded.JavaMajorVersion);
        Assert.Equal("8", reloaded.Overrides["server.view-distance"]);
    }

    [Fact]
    public void EulaIsOnlyAcceptedWhenWritten()
    {
        Assert.False(EulaFile.IsAccepted(dir));
        File.WriteAllText(Path.Combine(dir, EulaFile.FileName), "eula=false\n");
        Assert.False(EulaFile.IsAccepted(dir));
        EulaFile.WriteAccepted(dir);
        Assert.True(EulaFile.IsAccepted(dir));
    }

    [Fact]
    public void ApplyWritesPropertiesAndScripts()
    {
        var catalog = PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));
        File.WriteAllLines(Path.Combine(dir, ServerPropertiesFile.FileName), ["motd=keep", "view-distance=12"]);

        ServerLaunch.Apply(dir, PresetResolver.Resolve(catalog.Get("low"), null), @"C:\Java\bin\java.exe");

        var props = ServerPropertiesFile.Load(Path.Combine(dir, ServerPropertiesFile.FileName));
        Assert.Equal("keep", props.Get("motd"));
        Assert.Equal("6", props.Get("view-distance"));
        Assert.Contains("\"C:\\Java\\bin\\java.exe\" -Xms4096M -Xmx4096M", File.ReadAllText(Path.Combine(dir, "start.bat")));
        Assert.Contains("-jar fabric-server-launch.jar nogui", File.ReadAllText(Path.Combine(dir, "start.sh")));
    }
}

public class MetaParsingTests
{
    [Fact]
    public void PicksLatestStableOrFallsBackToNewest()
    {
        Assert.Equal("0.19.5", FabricMetaClient.PickLatestStable([new("0.20.0", false), new("0.19.5", true), new("0.19.4", true)])!.Version);
        Assert.Equal("0.1-beta", FabricMetaClient.PickLatestStable([new("0.1-beta", false)])!.Version);
        Assert.Null(FabricMetaClient.PickLatestStable([]));
    }

    [Fact]
    public void ReadsRequiredJavaMajorDefaultingTo8()
    {
        using var modern = JsonDocument.Parse("""{"javaVersion": {"component": "java-runtime-epsilon", "majorVersion": 25}}""");
        using var legacy = JsonDocument.Parse("""{"id": "1.12.2"}""");
        Assert.Equal(25, MojangClient.ReadJavaMajor(modern.RootElement));
        Assert.Equal(8, MojangClient.ReadJavaMajor(legacy.RootElement));
    }

    [Fact]
    public void ParsesLinuxMemInfo()
    {
        var values = HardwareProbe.ParseMemInfo(["MemTotal:       16314536 kB", "MemAvailable:    8123456 kB", "HugePages_Total:       0"]);
        Assert.Equal(16314536L * 1024, values["MemTotal"]);
        Assert.Equal(8123456L * 1024, values["MemAvailable"]);
        Assert.Equal(0, values["HugePages_Total"]);
    }
}
