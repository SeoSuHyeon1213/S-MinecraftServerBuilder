using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Builder.Core;
using Builder.Core.Http;
using Builder.Core.Mods;
using Builder.Core.Presets;
using Builder.Core.Server;
using Builder.Storage.Collection;

namespace Builder.Gui;

/// <summary>
/// 서버 관리 창. 서버를 콘솔 창 없이 실행하고, 창을 닫으면 <c>stop</c>으로 월드를 저장한 뒤 서버가 끝나야 창이 닫힌다.
/// 저사양 PC를 고려해 서버 출력은 큐에 모았다가 짧은 주기로 한꺼번에 화면에 붙인다.
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>저장 후 종료를 기다리다 강제 종료 여부를 묻는 간격. 저사양 PC의 월드 저장 시간을 고려한다.</summary>
    private static readonly TimeSpan StopWaitStep = TimeSpan.FromSeconds(60);
    private const int ConsoleMaxChars = 300_000;

    private enum ServerState { Stopped, Starting, Running, Stopping }

    private readonly BuilderPaths paths = BuilderPaths.Default();
    private readonly HttpClient http = BuilderHttp.CreateClient();
    private readonly ServerService service;
    private readonly GuiSettings settings;
    private readonly ConcurrentQueue<(string Line, ConsoleEvent? Event)> pending = new();
    private readonly SortedSet<string> players = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> history = [];
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 250 };

    private RunningServer? server;
    private CancellationTokenSource? startCancel;
    private ProcessProbe? probe;
    private ServerState state = ServerState.Stopped;
    private DateTime startedAt;
    private DateTime lastProbe;
    private int? maxPlayers;
    private bool closeAfterStop;
    private int historyIndex;

    private readonly TextBox folderBox = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Button browseButton = new() { Text = "찾기…", AutoSize = true };
    private readonly Button startButton = new() { Text = "시작", AutoSize = true };
    private readonly Button saveButton = new() { Text = "지금 저장", AutoSize = true };
    private readonly Button stopButton = new() { Text = "저장 후 종료", AutoSize = true };
    private readonly Label stateLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 0, 0, 0) };

    private readonly Label uptimeValue = ValueLabel();
    private readonly Label tpsValue = ValueLabel();
    private readonly Label msptValue = ValueLabel();
    private readonly Label memoryValue = ValueLabel();
    private readonly Label cpuValue = ValueLabel();
    private readonly Label playersValue = ValueLabel();

    private readonly ListBox playerList = new() { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "접속자 목록" };
    private readonly TextBox nameBox = new() { Dock = DockStyle.Fill, PlaceholderText = "플레이어 이름", AccessibleName = "플레이어 이름" };
    private readonly Button kickButton = new() { Text = "강퇴", AutoSize = true };
    private readonly Button opButton = new() { Text = "OP 주기", AutoSize = true };
    private readonly Button deopButton = new() { Text = "OP 해제", AutoSize = true };
    private readonly Button whitelistAddButton = new() { Text = "화이트리스트 추가", AutoSize = true };
    private readonly Button whitelistRemoveButton = new() { Text = "화이트리스트 제거", AutoSize = true };

    private readonly TextBox consoleBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9.5f), BackColor = SystemColors.Window, AccessibleName = "서버 콘솔 출력",
        // 기본값 32,767자는 AppendText에도 적용돼, 모드가 많은 서버는 시작 로그만으로 차서 이후 출력이 안 붙는다.
        MaxLength = ConsoleMaxChars * 2,
    };
    private readonly TextBox commandBox = new() { Dock = DockStyle.Fill, PlaceholderText = "서버 명령 (예: say 안녕하세요, list)", AccessibleName = "서버 명령" };
    private readonly Button sendButton = new() { Text = "보내기", AutoSize = true };

    public MainForm(string? initialDir)
    {
        Text = "Minecraft 서버 관리";
        Font = new Font("Malgun Gothic", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(900, 560);
        Size = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;

        service = new ServerService(http, LoadPresets(), LoadMods(), paths, line => Enqueue($"[mchb] {line}"));
        settings = GuiSettings.Load(paths);
        folderBox.Text = initialDir is not null ? Path.GetFullPath(initialDir) : settings.LastServerDir ?? "";

        BuildLayout();
        WireEvents();
        SetState(ServerState.Stopped);
        uiTimer.Start();
    }

    private static PresetCatalog LoadPresets() =>
        PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));

    private static ModCatalog LoadMods() =>
        ModCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Mods", "catalog.json"));

    private static Label ValueLabel() => new() { AutoSize = true, Text = "-", Anchor = AnchorStyles.Left };

    private void BuildLayout()
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 7, Padding = new Padding(6) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "서버 폴더", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(folderBox, 1, 0);
        top.Controls.Add(browseButton, 2, 0);
        top.Controls.Add(startButton, 3, 0);
        top.Controls.Add(saveButton, 4, 0);
        top.Controls.Add(stopButton, 5, 0);
        top.Controls.Add(stateLabel, 6, 0);

        var status = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddStatusRow(status, "가동 시간", uptimeValue);
        AddStatusRow(status, "TPS", tpsValue);
        AddStatusRow(status, "MSPT 평균 / 95%", msptValue);
        AddStatusRow(status, "메모리 (프로세스 / 힙)", memoryValue);
        AddStatusRow(status, "CPU", cpuValue);
        AddStatusRow(status, "접속자", playersValue);
        var statusGroup = new GroupBox { Text = "상태", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
        statusGroup.Controls.Add(status);

        var playerButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true };
        playerButtons.Controls.AddRange([kickButton, opButton, deopButton, whitelistAddButton, whitelistRemoveButton]);
        var nameRow = new Panel { Dock = DockStyle.Bottom, Height = nameBox.PreferredHeight + 6, Padding = new Padding(0, 3, 0, 3) };
        nameRow.Controls.Add(nameBox);
        var playerGroup = new GroupBox { Text = "접속자 관리", Dock = DockStyle.Fill, Padding = new Padding(8) };
        playerGroup.Controls.Add(playerList);
        playerGroup.Controls.Add(nameRow);
        playerGroup.Controls.Add(playerButtons);

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 6) };
        left.Controls.Add(playerGroup);
        left.Controls.Add(statusGroup);

        var commandRow = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 0) };
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        commandRow.Controls.Add(commandBox, 0, 0);
        commandRow.Controls.Add(sendButton, 1, 0);
        var consoleGroup = new GroupBox { Text = "서버 콘솔", Dock = DockStyle.Fill, Padding = new Padding(8) };
        consoleGroup.Controls.Add(consoleBox);
        consoleGroup.Controls.Add(commandRow);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 6, 6) };
        right.Controls.Add(consoleGroup);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        Controls.Add(top);
        Shown += (_, _) => split.SplitterDistance = 330;
    }

    private static void AddStatusRow(TableLayoutPanel table, string name, Label value)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 12, 4) }, 0, row);
        value.Margin = new Padding(3, 4, 3, 4);
        table.Controls.Add(value, 1, row);
    }

    private void WireEvents()
    {
        browseButton.Click += (_, _) => BrowseFolder();
        startButton.Click += async (_, _) => await StartServerAsync();
        stopButton.Click += async (_, _) => await StopServerAsync();
        saveButton.Click += async (_, _) => await SendAsync(ServerConsole.SaveAll);
        sendButton.Click += async (_, _) => await SendTypedCommandAsync();
        commandBox.KeyDown += async (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    await SendTypedCommandAsync();
                    break;
                case Keys.Up or Keys.Down when history.Count > 0:
                    e.SuppressKeyPress = true;
                    historyIndex = Math.Clamp(historyIndex + (e.KeyCode == Keys.Up ? -1 : 1), 0, history.Count);
                    commandBox.Text = historyIndex < history.Count ? history[historyIndex] : "";
                    commandBox.SelectionStart = commandBox.TextLength;
                    break;
            }
        };
        playerList.SelectedIndexChanged += (_, _) =>
        {
            if (playerList.SelectedItem is string name)
                nameBox.Text = name;
        };
        kickButton.Click += async (_, _) => await PlayerCommandAsync(ServerConsole.Kick, null);
        opButton.Click += async (_, _) => await PlayerCommandAsync(ServerConsole.Op, "OP는 모든 명령을 쓸 수 있습니다. '{0}'에게 OP 권한을 줄까요?");
        deopButton.Click += async (_, _) => await PlayerCommandAsync(ServerConsole.Deop, null);
        whitelistAddButton.Click += async (_, _) => await PlayerCommandAsync(ServerConsole.WhitelistAdd, null);
        whitelistRemoveButton.Click += async (_, _) => await PlayerCommandAsync(ServerConsole.WhitelistRemove, null);
        uiTimer.Tick += (_, _) => OnUiTick();
        FormClosing += OnFormClosing;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = $"서버 폴더 ({ServerInstanceConfig.FileName}가 있는 폴더)",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(folderBox.Text) ? folderBox.Text : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            folderBox.Text = dialog.SelectedPath;
    }

    private async Task StartServerAsync()
    {
        var dir = folderBox.Text.Trim();
        if (!File.Exists(Path.Combine(dir, ServerInstanceConfig.FileName)))
        {
            Warn($"빌더가 만든 서버 폴더가 아닙니다 ({ServerInstanceConfig.FileName} 없음).\n서버는 'mchb create'로 먼저 만드세요.");
            return;
        }
        // EULA는 사용자가 직접 동의해야 하므로 여기서 대신 동의하지 않는다 (AGENTS.md 규칙).
        if (!EulaFile.IsAccepted(dir))
        {
            Warn($"Minecraft EULA({EulaFile.EulaUrl}) 동의가 확인되지 않습니다.\n" +
                 $"내용을 읽고 동의한다면 서버 폴더의 {EulaFile.FileName}에서 eula=true로 바꾼 뒤 다시 시작하세요.");
            return;
        }

        settings.LastServerDir = dir;
        settings.Save(paths);
        consoleBox.Clear();
        players.Clear();
        maxPlayers = null;
        ResetStatus();
        startCancel = new CancellationTokenSource();
        SetState(ServerState.Starting);
        try
        {
            var token = startCancel.Token;
            server = await Task.Run(() => service.StartManagedAsync(dir, allowJavaDownload: true, Enqueue, Monitor, token));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or HttpRequestException
                                       or InvalidDataException or PlatformNotSupportedException or OperationCanceledException
                                       or System.ComponentModel.Win32Exception)
        {
            Enqueue($"[mchb] 시작 실패: {ex.Message}");
            SetState(ServerState.Stopped);
            if (closeAfterStop)
                Close();
            else
                Warn($"서버를 시작하지 못했습니다.\n{ex.Message}");
            return;
        }
        finally
        {
            startCancel.Dispose();
            startCancel = null;
        }

        probe = new ProcessProbe(server.Context.Process);
        startedAt = DateTime.Now;
        SetState(ServerState.Starting);
        if (!server.Context.Preset.MetricsEnabled)
            tpsValue.Text = msptValue.Text = "수집 꺼짐";
        maxPlayers = int.TryParse(ServerPropertiesFile.Load(Path.Combine(dir, ServerPropertiesFile.FileName)).Get("max-players"), out var max) ? max : null;
        ShowPlayers();
        if (closeAfterStop)
            _ = StopServerAsync();
        await WatchExitAsync(server);
    }

    /// <summary>지표 수집이 켜진 서버는 CLI와 같은 수집기로 DB에 기록하면서 값을 창에도 보여 준다.</summary>
    private Task Monitor(ServerRunContext context, CancellationToken ct) => context.Preset.MetricsEnabled
        ? MetricsCollector.MonitorAsync(context, line => Enqueue($"[mchb] {line}"), ct, metrics => BeginInvokeSafe(() => ShowMetrics(metrics)))
        : Task.CompletedTask;

    private async Task WatchExitAsync(RunningServer running)
    {
        var code = await running.Completion;
        Enqueue($"[mchb] 서버 종료 (코드 {code})");
        await running.DisposeAsync();
        server = null;
        probe = null;
        players.Clear();
        OnUiTick();
        SetState(ServerState.Stopped);
        UnblockShutdown();
        if (closeAfterStop)
            Close();
    }

    /// <summary>stop을 보내고 끝나기를 기다린다. 오래 걸리면 계속 기다릴지, 저장 없이 강제 종료할지 묻는다.</summary>
    private async Task StopServerAsync()
    {
        var running = server;
        if (running is null)
        {
            // Java 준비(다운로드) 중이면 시작을 취소한다.
            startCancel?.Cancel();
            return;
        }
        if (running.HasExited || state == ServerState.Stopping)
            return;
        SetState(ServerState.Stopping);
        Enqueue("[mchb] 월드를 저장하고 서버를 종료합니다...");
        while (!await running.StopAsync(StopWaitStep))
        {
            var answer = MessageBox.Show(this,
                $"{StopWaitStep.TotalSeconds:0}초가 지나도 서버가 끝나지 않았습니다. 월드 저장이 오래 걸리고 있을 수 있습니다.\n\n" +
                "[예] 계속 기다리기 (권장)\n[아니요] 강제 종료 — 마지막 저장 이후의 변경이 사라질 수 있습니다",
                "서버 종료 대기", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.No)
            {
                Enqueue("[mchb] 사용자가 강제 종료를 선택했습니다.");
                running.Kill();
                break;
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (state == ServerState.Stopped)
        {
            uiTimer.Stop();
            http.Dispose();
            return;
        }

        // 서버가 돌고 있으면 창을 바로 닫지 않는다. 저장 후 서버가 끝나면 WatchExitAsync가 창을 닫는다.
        e.Cancel = true;
        if (closeAfterStop)
            return;
        // X 버튼·Alt+F4는 UserClosing이다. Windows 종료·작업 관리자의 창 닫기 등은 묻지 않고 바로 저장 후 종료한다.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "서버가 실행 중입니다. 월드를 저장하고 서버를 끈 뒤 창을 닫을까요?",
                "서버 실행 중", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
                return;
        }
        else if (e.CloseReason == CloseReason.WindowsShutDown)
        {
            BlockShutdown("Minecraft 서버가 월드를 저장하고 있습니다");
        }

        // Java 준비 중이면 시작을 취소하고, 이미 프로세스가 떴으면 StartServerAsync가 closeAfterStop을 보고 이어서 종료한다.
        closeAfterStop = true;
        _ = StopServerAsync();
    }

    private async Task SendTypedCommandAsync()
    {
        var text = commandBox.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (await SendAsync(text))
        {
            history.Remove(text);
            history.Add(text);
            historyIndex = history.Count;
            commandBox.Clear();
        }
    }

    private async Task PlayerCommandAsync(Func<string, string> command, string? confirm)
    {
        var name = nameBox.Text.Trim();
        if (!ServerConsole.IsValidPlayerName(name))
        {
            Warn("플레이어 이름이 올바르지 않습니다. 목록에서 고르거나 이름을 입력하세요.\n(영문·숫자·밑줄 16자 이하, 베드락 플레이어는 앞에 '.')");
            return;
        }
        if (confirm is not null && MessageBox.Show(this, string.Format(confirm, name), "확인",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        await SendAsync(command(name));
    }

    private async Task<bool> SendAsync(string command)
    {
        if (server is null || server.HasExited)
            return false;
        try
        {
            var line = ServerConsole.NormalizeCommand(command);
            Enqueue($"> {line}");
            await server.SendCommandAsync(line);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Warn(ex.Message);
            return false;
        }
    }

    /// <summary>읽기 스레드에서 호출된다. 해석까지 여기서 하고 화면 반영은 타이머가 한다.</summary>
    private void Enqueue(string line) => pending.Enqueue((line, ServerConsole.Parse(line)));

    private void OnUiTick()
    {
        if (!pending.IsEmpty)
        {
            var text = new System.Text.StringBuilder();
            var playersChanged = false;
            while (pending.TryDequeue(out var item))
            {
                text.Append(item.Line).Append("\r\n");
                switch (item.Event)
                {
                    case PlayerJoined joined:
                        playersChanged |= players.Add(joined.Name);
                        break;
                    case PlayerLeft left:
                        playersChanged |= players.Remove(left.Name);
                        break;
                    case ServerReady when state == ServerState.Starting:
                        SetState(ServerState.Running);
                        break;
                    case ServerStopping when state != ServerState.Stopped:
                        SetState(ServerState.Stopping);
                        break;
                }
            }
            AppendConsole(text.ToString());
            if (playersChanged)
                ShowPlayers();
        }

        if (server is not null)
        {
            var up = DateTime.Now - startedAt;
            uptimeValue.Text = up.Days > 0 ? $@"{up.Days}일 {up:hh\:mm\:ss}" : up.ToString(@"hh\:mm\:ss");
            // 프로세스 메모리·CPU는 지표 수집 주기와 별개로 2초마다 직접 잰다 (가벼운 OS 조회).
            if (probe is not null && DateTime.Now - lastProbe >= TimeSpan.FromSeconds(2))
            {
                lastProbe = DateTime.Now;
                if (probe.Measure() is { } snapshot)
                {
                    memoryValue.Text = $"{snapshot.RssMb:N0}MB / {server.Context.Preset.HeapMb:N0}MB";
                    cpuValue.Text = snapshot.CpuPercent is { } cpu ? $"{cpu:0.0}%" : "-";
                }
            }
        }
    }

    private void AppendConsole(string text)
    {
        if (consoleBox.TextLength + text.Length > ConsoleMaxChars)
        {
            // 오래 켜 둬도 메모리가 늘지 않도록 앞부분을 버린다.
            var keep = consoleBox.Text[^Math.Min(consoleBox.TextLength, ConsoleMaxChars / 2)..];
            consoleBox.Text = keep[(keep.IndexOf('\n') + 1)..];
        }
        consoleBox.AppendText(text);
    }

    private void ShowMetrics(LiveMetrics metrics)
    {
        if (server is null)
            return;
        if (metrics.Tick is { } tick)
        {
            tpsValue.Text = tick.Tps is { } tps ? $"{tps:0.0}" : "-";
            msptValue.Text = $"{Format(tick.MsptAvg)} / {Format(tick.MsptP95)} ms";
        }
        if (metrics.Players is { } list)
        {
            // 콘솔 해석으로 놓친 변화가 있어도 /list 결과로 다시 맞춘다.
            maxPlayers = list.Max;
            players.Clear();
            players.UnionWith(list.Names);
            ShowPlayers();
        }

        static string Format(double? value) => value is { } v ? $"{v:0.0}" : "-";
    }

    private void ShowPlayers()
    {
        var selected = playerList.SelectedItem as string;
        playerList.BeginUpdate();
        playerList.Items.Clear();
        foreach (var name in players)
            playerList.Items.Add(name);
        if (selected is not null && players.Contains(selected))
            playerList.SelectedItem = selected;
        playerList.EndUpdate();
        playersValue.Text = maxPlayers is { } max ? $"{players.Count} / {max}" : $"{players.Count}";
    }

    private void ResetStatus()
    {
        foreach (var label in new[] { uptimeValue, tpsValue, msptValue, memoryValue, cpuValue, playersValue })
            label.Text = "-";
        playerList.Items.Clear();
    }

    private void SetState(ServerState next)
    {
        state = next;
        var running = next is ServerState.Starting or ServerState.Running;
        stateLabel.Text = next switch
        {
            ServerState.Stopped => "● 중지됨",
            ServerState.Starting => "● 시작 중",
            ServerState.Running => "● 실행 중",
            _ => "● 저장 후 종료 중",
        };
        stateLabel.ForeColor = next switch
        {
            ServerState.Running => Color.SeaGreen,
            ServerState.Stopped => SystemColors.GrayText,
            _ => Color.DarkOrange,
        };
        browseButton.Enabled = startButton.Enabled = next == ServerState.Stopped;
        stopButton.Enabled = running;
        foreach (var control in new Control[] { saveButton, sendButton, commandBox, kickButton, opButton, deopButton, whitelistAddButton, whitelistRemoveButton })
            control.Enabled = running;
        if (next == ServerState.Stopped)
            ResetStatus();
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 창이 닫히는 중
        }
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    // Windows 종료·로그오프 때 서버 저장이 끝날 때까지 종료 화면에 이유를 보여 준다.
    private void BlockShutdown(string reason)
    {
        if (OperatingSystem.IsWindows())
            ShutdownBlockReasonCreate(Handle, reason);
    }

    private void UnblockShutdown()
    {
        if (OperatingSystem.IsWindows() && IsHandleCreated)
            ShutdownBlockReasonDestroy(Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            uiTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
