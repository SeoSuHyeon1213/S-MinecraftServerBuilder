using Builder.Core.Loaders;
using Builder.Core.Presets;
using Builder.Core.Rcon;
using Builder.Core.Server;

namespace Builder.Core.Tests;

public class CrossplayTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mchb-crossplay-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static ServerInstanceConfig Config(bool enabled, int port = Crossplay.DefaultBedrockPort) => new()
    {
        MinecraftVersion = "26.3", LoaderVersion = "0.19.5",
        Crossplay = { Enabled = enabled, BedrockPort = port },
    };

    [Fact]
    public void AddsCrossplayModsOnlyWhenEnabled()
    {
        IReadOnlyList<string> preset = ["lithium", "spark"];

        Assert.Equal(preset, Crossplay.WantedMods(preset, Config(enabled: false)));
        Assert.Equal(["lithium", "spark", "geyser", "floodgate"], Crossplay.WantedMods(preset, Config(enabled: true)));
        Assert.Equal(["geyser", "floodgate"], Crossplay.WantedMods(["geyser"], Config(enabled: true)));
    }

    [Fact]
    public void PassesBedrockPortThroughGeyserEnvironmentVariable()
    {
        Assert.Empty(Crossplay.Environment(Config(enabled: false, port: 19200)));
        var environment = Crossplay.Environment(Config(enabled: true, port: 19200));
        Assert.Equal("19200", environment["GEYSER_BEDROCK_PORT"]);
        Assert.Equal("floodgate", environment["GEYSER_JAVA_AUTH_TYPE"]);
    }

    [Fact]
    public void ScriptsSetEnvironmentBeforeJava()
    {
        var catalog = PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));
        var resolved = PresetResolver.Resolve(catalog.Get("low"), null);
        var loader = new FabricServerLoader(new HttpClient());

        ServerLaunch.Apply(dir, resolved, "java", loader, "0.19.5", Crossplay.Environment(Config(enabled: true, port: 19133)));

        var bat = File.ReadAllText(Path.Combine(dir, "start.bat"));
        var sh = File.ReadAllText(Path.Combine(dir, "start.sh"));
        Assert.Contains("set \"GEYSER_BEDROCK_PORT=19133\"\r\nset \"GEYSER_JAVA_AUTH_TYPE=floodgate\"\r\n\"java\"", bat);
        Assert.Contains("export GEYSER_BEDROCK_PORT='19133'\nexport GEYSER_JAVA_AUTH_TYPE='floodgate'\nexec \"java\"", sh);

        ServerLaunch.Apply(dir, resolved, "java", loader, "0.19.5", Crossplay.Environment(Config(enabled: false)));
        Assert.DoesNotContain("GEYSER", File.ReadAllText(Path.Combine(dir, "start.bat")));
        Assert.DoesNotContain("GEYSER", File.ReadAllText(Path.Combine(dir, "start.sh")));
    }

    [Fact]
    public void RejectsBedrockPortThatCollidesWithJavaOrRcon()
    {
        var path = Path.Combine(dir, ServerPropertiesFile.FileName);
        File.WriteAllLines(path, ["server-port=25565", "enable-rcon=true", "rcon.port=19132", "rcon.password=x"]);
        var properties = ServerPropertiesFile.Load(path);

        Assert.Throws<ArgumentException>(() => Crossplay.ValidatePort(25565, properties));
        Assert.Throws<ArgumentException>(() => Crossplay.ValidatePort(19132, properties));
        Assert.Throws<ArgumentException>(() => Crossplay.ValidatePort(0, properties));
        Crossplay.ValidatePort(19133, properties);

        // RCON이 꺼져 있으면 rcon.port 값은 쓰이지 않으므로 충돌로 보지 않는다.
        File.WriteAllLines(path, ["enable-rcon=false", "rcon.port=19132"]);
        Crossplay.ValidatePort(19132, ServerPropertiesFile.Load(path));
        Assert.Null(RconSettings.Read(ServerPropertiesFile.Load(path)));
    }

    [Fact]
    public void BuilderJsonRoundTripsCrossplayAndOldFilesReadAsDisabled()
    {
        File.WriteAllText(Path.Combine(dir, ServerInstanceConfig.FileName),
            """{ "schemaVersion": 2, "minecraftVersion": "26.3", "loader": "fabric", "loaderVersion": "0.19.5" }""");
        var old = ServerInstanceConfig.Load(dir);
        Assert.False(old.Crossplay.Enabled);
        Assert.Equal(Crossplay.DefaultBedrockPort, old.Crossplay.BedrockPort);

        Config(enabled: true, port: 19140).Save(dir);
        var loaded = ServerInstanceConfig.Load(dir);
        Assert.True(loaded.Crossplay.Enabled);
        Assert.Equal(19140, loaded.Crossplay.BedrockPort);
    }
}
