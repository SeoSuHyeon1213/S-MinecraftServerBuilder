using System.Diagnostics;
using Builder.Core.Presets;
using Builder.Core.Server;

namespace Builder.Core.Tests;

public class ServerConsoleTests
{
    [Theory]
    [InlineData("[21:32:40] [Server thread/INFO]: Steve joined the game")]
    [InlineData("[21:32:40] [Server thread/INFO] (Minecraft) Steve joined the game")]
    [InlineData("[21:32:40] [Server thread/INFO] [minecraft/MinecraftServer]: Steve joined the game")]
    public void ParsesJoinInVanillaFabricAndNeoForgeFormats(string line) =>
        Assert.Equal(new PlayerJoined("Steve"), ServerConsole.Parse(line));

    [Fact]
    public void ParsesLeaveReadyAndStopping()
    {
        Assert.Equal(new PlayerLeft(".BedrockUser"), ServerConsole.Parse("[10:00:00] [Server thread/INFO]: .BedrockUser left the game"));
        Assert.Equal(new ServerReady(12.5), ServerConsole.Parse("[10:00:00] [Server thread/INFO]: Done (12.5s)! For help, type \"help\""));
        Assert.Equal(new ServerReady(3.25), ServerConsole.Parse("[10:00:00] [Server thread/INFO]: Done (3,25s)! For help, type \"help\""));
        Assert.IsType<ServerStopping>(ServerConsole.Parse("[10:00:00] [Server thread/INFO]: Stopping server"));
        Assert.IsType<ServerStopping>(ServerConsole.Parse("[10:00:00] [Server thread/INFO]: Stopping the server"));
    }

    [Theory]
    [InlineData("[10:00:00] [Server thread/INFO]: <Alex> Steve joined the game")]
    [InlineData("[10:00:00] [Server thread/INFO]: [Not Secure] <Alex> Steve left the game")]
    [InlineData("[10:00:00] [main/INFO]: Steve joined the game")]
    [InlineData("[10:00:00] [Server thread/WARN]: Steve joined the game")]
    [InlineData("Steve joined the game")]
    [InlineData("[10:00:00] [Server thread/INFO]: Steve[/127.0.0.1:50000] logged in with entity id 1")]
    public void IgnoresChatOtherThreadsAndUnrelatedLines(string line) => Assert.Null(ServerConsole.Parse(line));

    [Theory]
    [InlineData("/say hi", "say hi")]
    [InlineData("  list  ", "list")]
    public void NormalizesCommands(string input, string expected) => Assert.Equal(expected, ServerConsole.NormalizeCommand(input));

    [Theory]
    [InlineData("")]
    [InlineData("  / ")]
    [InlineData("say hi\nstop")]
    [InlineData("say hi\rstop")]
    public void RejectsEmptyOrMultiLineCommands(string input) =>
        Assert.Throws<ArgumentException>(() => ServerConsole.NormalizeCommand(input));

    [Theory]
    [InlineData("Steve", true)]
    [InlineData("a_B_9", true)]
    [InlineData(".Bedrock_Name", true)]
    [InlineData("TwelveChars_123456", false)]
    [InlineData("Steve stop", false)]
    [InlineData("Steve\nstop", false)]
    [InlineData("@a", false)]
    [InlineData("", false)]
    public void ValidatesPlayerNames(string name, bool valid) => Assert.Equal(valid, ServerConsole.IsValidPlayerName(name));

    [Fact]
    public void PlayerCommandsRejectInvalidNames()
    {
        Assert.Equal("whitelist add .Bedrock", ServerConsole.WhitelistAdd(".Bedrock"));
        Assert.Throws<ArgumentException>(() => ServerConsole.Kick("@a"));
        Assert.Throws<ArgumentException>(() => ServerConsole.Op("Steve\nstop"));
    }
}

/// <summary>
/// 진짜 Java 서버 대신, 표준 입력에서 'stop'을 받을 때까지 줄을 되돌려 주는 작은 프로세스로 실행·종료 흐름을 확인한다.
/// </summary>
public class RunningServerTests
{
    private const string FakeServerScript =
        "[Console]::Out.WriteLine('[00:00:00] [Server thread/INFO]: Done (1.0s)! For help, type \"help\"'); " +
        "while (($l = [Console]::In.ReadLine()) -ne $null) { " +
        "  if ($l -eq 'stop') { [Console]::Out.WriteLine('[00:00:01] [Server thread/INFO]: Stopping server'); Start-Sleep -Milliseconds 300; exit 0 } " +
        "  [Console]::Out.WriteLine(\"got:$l\") }; exit 3";

    private static RunningServer StartFake(List<string> output, string script = FakeServerScript,
        Func<ServerRunContext, CancellationToken, Task>? monitor = null)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
            psi.ArgumentList.Add(arg);
        var process = Process.Start(psi)!;
        var context = new ServerRunContext("fake", new ServerInstanceConfig { MinecraftVersion = "26.3", LoaderVersion = "0.20.1" }, null!, process);
        return RunningServer.Attach(context, line => { lock (output) output.Add(line); }, monitor);
    }

    [Fact]
    public async Task StopSendsStopOnceAndWaitsForExitWithAllOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var output = new List<string>();
        var monitorCancelled = new TaskCompletionSource();
        var server = StartFake(output, monitor: async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { monitorCancelled.SetResult(); }
        });

        await server.SendCommandAsync("/say hi");
        Assert.True(await server.StopAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await server.StopAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(0, await server.Completion);
        Assert.True(server.StopRequested);
        Assert.True(monitorCancelled.Task.IsCompleted);
        lock (output)
        {
            Assert.Contains("got:say hi", output);
            Assert.Equal("[00:00:01] [Server thread/INFO]: Stopping server", output[^1]);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendCommandAsync("list"));
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StopReturnsFalseWhenServerDoesNotExitAndKillEndsIt()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var output = new List<string>();
        // stop을 무시하는 멈춘 서버
        var server = StartFake(output, "while (($l = [Console]::In.ReadLine()) -ne $null) { }; Start-Sleep -Seconds 60");

        Assert.False(await server.StopAsync(TimeSpan.FromMilliseconds(500)));
        Assert.False(server.HasExited);
        server.Kill();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(server.HasExited);
        await server.DisposeAsync();
    }
}
