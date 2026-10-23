using System.Diagnostics;
using Builder.Core.Hardware;
using Builder.Core.Java;
using Builder.Core.Loaders;
using Builder.Core.Minecraft;
using Builder.Core.Mods;
using Builder.Core.Presets;
using Builder.Core.Rcon;

namespace Builder.Core.Server;

public sealed record CreateServerRequest(
    string Directory,
    string? MinecraftVersion,
    string? LoaderVersion,
    string Preset,
    IReadOnlyDictionary<string, string> Overrides,
    bool EulaAccepted,
    bool AllowJavaDownload,
    bool InstallMods = true,
    bool AllowUnstableMods = false,
    string ModLoader = ModLoaders.Fabric);

/// <param name="RemovedManagedMods">빌더가 설치했다가 이전 로더용이라 지운 모드.</param>
/// <param name="DisabledUserMods">사용자가 넣은 이전 로더 전용 jar (mods-disabled/&lt;이전 로더&gt;/ 로 옮김).</param>
/// <param name="RestoredUserMods">예전에 옮겨 두었다가 이번 로더로 돌아오면서 mods로 되돌린 jar.</param>
public sealed record LoaderSwitchReport(
    string FromLoader,
    string ToLoader,
    string LoaderVersion,
    IReadOnlyList<string> RemovedManagedMods,
    IReadOnlyList<string> DisabledUserMods,
    IReadOnlyList<string> RestoredUserMods,
    ModSyncReport? Mods);

/// <summary>실행 중인 서버 정보. <see cref="ServerService.RunAsync"/>의 모니터(지표 수집기 등)에 전달된다.</summary>
public sealed record ServerRunContext(string Directory, ServerInstanceConfig Config, ResolvedPreset Preset, Process Process);

/// <summary>서버 생성·재구성·로더 전환·실행. CLI와 이후 웹 서비스가 함께 쓴다.</summary>
public sealed class ServerService
{
    private readonly HttpClient http;
    private readonly PresetCatalog catalog;
    private readonly BuilderPaths paths;
    private readonly Action<string> log;
    private readonly MojangClient mojang;
    private readonly ModInstaller mods;
    private readonly Dictionary<string, IServerLoader> loaders;

    public ServerService(HttpClient http, PresetCatalog catalog, ModCatalog modCatalog, BuilderPaths paths, Action<string> log)
    {
        this.http = http;
        this.catalog = catalog;
        this.paths = paths;
        this.log = log;
        mojang = new MojangClient(http);
        // 모드 소스 우선순위. CurseForge는 추후 이 목록 뒤에 추가한다 (설계서 6장).
        mods = new ModInstaller(http, modCatalog, [new ModrinthSource(http)], log);
        // 모드 로더 목록. 로더를 추가할 때는 IServerLoader를 구현해 여기에 등록한다.
        loaders = new IServerLoader[] { new FabricServerLoader(http), new NeoForgeServerLoader(http, log) }
            .ToDictionary(l => l.Id, StringComparer.Ordinal);
    }

    /// <summary>로더 ID 또는 한글 이름(페브릭, 네오포지)으로 로더를 찾는다.</summary>
    public IServerLoader GetLoader(string idOrName) => loaders[ModLoaders.Normalize(idOrName)];

    public async Task<(ServerInstanceConfig Config, ModSyncReport? Mods)> CreateAsync(
        CreateServerRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(request.Directory);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException($"폴더가 비어 있지 않습니다: {dir}\n기존 서버의 설정을 바꾸려면 'configure', 로더를 바꾸려면 'switch-loader'를 사용하세요.");
        if (!request.EulaAccepted)
            throw new InvalidOperationException($"Minecraft EULA({EulaFile.EulaUrl})에 동의해야 서버를 만들 수 있습니다.");

        // 다운로드 전에 입력 오류를 먼저 확인한다.
        var preset = catalog.Get(request.Preset);
        var resolved = PresetResolver.Resolve(preset, request.Overrides);
        var loader = GetLoader(request.ModLoader);
        var loaderName = ModLoaders.DisplayName(loader.Id);

        var game = request.MinecraftVersion ?? await loader.GetLatestGameVersionAsync(ct);
        var plan = await loader.ResolveAsync(game, request.LoaderVersion, ct);
        var javaMajor = await mojang.GetRequiredJavaMajorAsync(game, ct);
        log($"Minecraft {game} / {loaderName} {plan.LoaderVersion}" +
            (plan.InstallerVersion is null ? "" : $" / 인스톨러 {plan.InstallerVersion}") + $" / 필요 Java {javaMajor}");

        var java = await EnsureJavaAsync(javaMajor, request.AllowJavaDownload, progress, ct);

        var createdDir = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        try
        {
            log($"{loaderName} 서버 설치 중...");
            await loader.InstallAsync(dir, game, plan, java.ExecutablePath, progress, ct);

            EulaFile.WriteAccepted(dir);

            var config = new ServerInstanceConfig
            {
                MinecraftVersion = game,
                Loader = loader.Id,
                LoaderVersion = plan.LoaderVersion,
                InstallerVersion = plan.InstallerVersion,
                JavaMajorVersion = javaMajor,
                JavaPath = java.ExecutablePath,
                Preset = preset.Id,
                Overrides = new Dictionary<string, string>(request.Overrides, StringComparer.Ordinal),
                ManageMods = request.InstallMods,
                AllowUnstableMods = request.AllowUnstableMods,
            };

            ModSyncReport? modReport = null;
            if (config.ManageMods)
            {
                log($"최적화 모드 확인 중 (Modrinth, {loaderName})...");
                modReport = await mods.SyncAsync(dir, config, preset.Mods, update: true, ct);
            }

            config.Save(dir);
            ServerLaunch.Apply(dir, resolved, java.ExecutablePath, loader, config.LoaderVersion);
            return (config, modReport);
        }
        catch
        {
            // 생성 도중 실패하면 다시 시도할 수 있도록 이번에 만든 파일만 정리한다. (시작 시 폴더는 비어 있었다.)
            if (createdDir)
                Directory.Delete(dir, recursive: true);
            else
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    if (File.Exists(entry)) File.Delete(entry);
                    else Directory.Delete(entry, recursive: true);
            throw;
        }
    }

    /// <summary>프리셋 변경·오버라이드 추가(set)·삭제(unset) 후 설정 파일과 스크립트를 다시 만든다.</summary>
    public async Task<(ServerInstanceConfig Config, ResolvedPreset Resolved, ModSyncReport? Mods)> ConfigureAsync(
        string serverDir, string? presetId, IReadOnlyDictionary<string, string> set, IReadOnlyCollection<string> unset,
        bool allowJavaDownload, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(serverDir);
        var config = ServerInstanceConfig.Load(dir);

        if (presetId is not null)
            config.Preset = catalog.Get(presetId).Id;
        foreach (var key in unset)
            config.Overrides.Remove(key);
        foreach (var (key, value) in set)
        {
            PresetResolver.ValidateKey(key);
            config.Overrides[key] = value;
        }

        var resolved = PresetResolver.Resolve(catalog.Get(config.Preset), config.Overrides);
        var java = await EnsureConfiguredJavaAsync(config, allowJavaDownload, progress, ct);

        // 프리셋이 바뀌면 모드 구성도 맞춘다. 이미 설치된 모드는 조회 없이 유지한다.
        ModSyncReport? modReport = null;
        if (config.ManageMods)
            modReport = await mods.SyncAsync(dir, config, resolved.Source.Mods, update: false, ct);

        config.Save(dir);
        ServerLaunch.Apply(dir, resolved, java, GetLoader(config.Loader), config.LoaderVersion);
        return (config, resolved, modReport);
    }

    /// <summary>
    /// 같은 Minecraft 버전에서 모드 로더를 바꾼다 (Fabric ↔ NeoForge).
    /// 1) 새 로더 설치 (실패하면 기존 구성은 바뀌지 않음) 2) 빌더가 설치한 이전 로더용 모드 삭제
    /// 3) 사용자 jar 중 이전 로더 전용은 mods-disabled로 옮기고, 예전에 옮겨 둔 새 로더용 jar는 되돌림
    /// 4) 시작 스크립트 재생성 5) 프리셋 최적화 모드를 새 로더용으로 설치
    /// 월드 폴더가 있으면 <paramref name="confirmed"/>가 true일 때만 진행한다.
    /// </summary>
    public async Task<LoaderSwitchReport> SwitchLoaderAsync(
        string serverDir, string modLoader, string? loaderVersion, bool confirmed, bool allowJavaDownload,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(serverDir);
        var config = ServerInstanceConfig.Load(dir);
        var target = GetLoader(modLoader);
        var from = config.Loader;
        var targetName = ModLoaders.DisplayName(target.Id);
        if (target.Id == from)
            throw new InvalidOperationException($"이미 {targetName} 서버입니다.");

        var world = ServerInstanceConfig.WorldDirectory(dir);
        if (Directory.Exists(world) && !confirmed)
            throw new InvalidOperationException(
                $"월드 폴더가 있습니다: {world}\n" +
                $"로더를 바꾸면 {ModLoaders.DisplayName(from)} 전용 모드가 추가한 블록·아이템·엔티티는 월드에서 사라질 수 있습니다 (바닐라 요소는 유지).\n" +
                "월드 폴더를 백업한 뒤 --confirm 을 붙여 다시 실행하세요.");

        var preset = catalog.Get(config.Preset);
        var resolved = PresetResolver.Resolve(preset, config.Overrides);
        var plan = await target.ResolveAsync(config.MinecraftVersion, loaderVersion, ct);
        var java = await EnsureConfiguredJavaAsync(config, allowJavaDownload, progress, ct);

        log($"{ModLoaders.DisplayName(from)} → {targetName} {plan.LoaderVersion} (Minecraft {config.MinecraftVersion})");
        log($"{targetName} 서버 설치 중...");
        await target.InstallAsync(dir, config.MinecraftVersion, plan, java, progress, ct);

        var modsDir = Path.Combine(dir, ModInstaller.ModsDirName);
        var removed = new List<string>();
        foreach (var mod in config.Mods)
        {
            var path = Path.Combine(modsDir, mod.FileName);
            if (File.Exists(path))
                File.Delete(path);
            removed.Add($"{mod.Name} {mod.VersionNumber}");
        }
        config.Mods.Clear();

        var (disabled, restored) = UserModShelf.Swap(dir, from, target.Id);

        config.Loader = target.Id;
        config.LoaderVersion = plan.LoaderVersion;
        config.InstallerVersion = plan.InstallerVersion;
        // 모드 설치가 중간에 실패해도 로더 상태가 맞도록 먼저 저장한다.
        config.Save(dir);
        ServerLaunch.Apply(dir, resolved, java, target, config.LoaderVersion);

        ModSyncReport? modReport = null;
        if (config.ManageMods)
        {
            log($"최적화 모드 확인 중 (Modrinth, {targetName})...");
            modReport = await mods.SyncAsync(dir, config, preset.Mods, update: true, ct);
            config.Save(dir);
        }

        return new LoaderSwitchReport(from, target.Id, plan.LoaderVersion, removed, disabled, restored, modReport);
    }

    /// <summary>프리셋 모드를 최신 호환 버전으로 다시 확인·설치한다. 자동 모드 관리가 꺼져 있었다면 다시 켠다.</summary>
    public async Task<(ServerInstanceConfig Config, ModSyncReport Mods)> UpdateModsAsync(
        string serverDir, bool? allowUnstable, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(serverDir);
        var config = ServerInstanceConfig.Load(dir);
        if (allowUnstable is not null)
            config.AllowUnstableMods = allowUnstable.Value;
        config.ManageMods = true;

        var preset = catalog.Get(config.Preset);
        log($"최적화 모드 확인 중 (Modrinth, Minecraft {config.MinecraftVersion} {ModLoaders.DisplayName(config.Loader)})...");
        var report = await mods.SyncAsync(dir, config, preset.Mods, update: true, ct);
        config.Save(dir);
        return (config, report);
    }

    /// <summary>
    /// 서버를 현재 콘솔에 붙여 실행하고 종료 코드를 돌려준다. 콘솔 입력은 서버로 그대로 전달된다.
    /// <paramref name="monitor"/>는 서버가 실행되는 동안 함께 돌고, 서버가 끝나면 취소된다.
    /// 모니터가 실패해도 서버 실행에는 영향을 주지 않는다 (설계서 2장 원칙).
    /// </summary>
    public async Task<int> RunAsync(string serverDir, bool allowJavaDownload,
        Func<ServerRunContext, CancellationToken, Task>? monitor = null, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(serverDir);
        var config = ServerInstanceConfig.Load(dir);
        if (!EulaFile.IsAccepted(dir))
            throw new InvalidOperationException($"{EulaFile.FileName}에서 EULA 동의가 확인되지 않습니다.");

        var resolved = PresetResolver.Resolve(catalog.Get(config.Preset), config.Overrides);
        var java = await EnsureConfiguredJavaAsync(config, allowJavaDownload, null, ct);
        config.Save(dir);

        // 지표 수집 기능 이전에 만든 서버도 configure 없이 수집되도록, 실행 전에 RCON 설정을 확인한다.
        if (resolved.MetricsEnabled)
        {
            var propertiesPath = Path.Combine(dir, ServerPropertiesFile.FileName);
            var properties = ServerPropertiesFile.Load(propertiesPath);
            if (RconSettings.EnsureEnabled(properties))
            {
                properties.Save(propertiesPath);
                log($"지표 수집을 위해 server.properties에서 RCON을 켰습니다 (포트 {properties.Get(RconSettings.PortKey)}). " +
                    $"끄려면 'configure --set {PresetResolver.MetricsIntervalKey}=0' 후 enable-rcon=false로 바꾸세요.");
            }
        }

        var loader = GetLoader(config.Loader);
        var psi = new ProcessStartInfo(java) { WorkingDirectory = dir, UseShellExecute = false };
        foreach (var argument in ServerLaunch.BuildArguments(resolved, loader.LaunchArguments(config.LoaderVersion, OperatingSystem.IsWindows())))
            psi.ArgumentList.Add(argument);

        // 실행을 막지는 않고, 지금 여유 RAM이 부족하면 다른 프로그램을 닫도록 안내만 한다.
        var availableGb = HardwareProbe.Detect().AvailableMemoryGb;
        var requiredGb = PresetRecommender.RequiredGbForHeap(resolved.HeapMb);
        if (availableGb < requiredGb)
            log($"주의: 현재 여유 RAM {availableGb:0.0}GB, 서버에 약 {requiredGb:0.0}GB 필요. " +
                $"브라우저 등 다른 프로그램을 닫아 {requiredGb - availableGb:0.0}GB를 더 확보하세요. (부족하면 버벅임·디스크 스와핑 발생)");

        // 모드가 많은데 힙이 작으면 OutOfMemoryError나 잦은 GC 멈춤이 생기기 쉽다. 경고만 하고 값은 바꾸지 않는다.
        var modCount = ModdedHeapAdvisor.CountModJars(dir);
        var advisedMb = ModdedHeapAdvisor.RecommendedHeapMb(config.Loader, modCount);
        if (resolved.HeapMb < advisedMb)
            log($"주의: 모드 {modCount}개 기준 권장 힙은 약 {advisedMb}MB인데 현재 {resolved.HeapMb}MB입니다. " +
                $"메모리 부족으로 멈추면 'configure \"{dir}\" --set {PresetResolver.HeapKey}={advisedMb}'로 늘리세요.");

        log($"서버 시작: {ModLoaders.DisplayName(config.Loader)} {config.LoaderVersion}, " +
            $"프리셋 {resolved.Source.DisplayName}({resolved.Source.Id}), 힙 {resolved.HeapMb}MB, 모드 {modCount}개");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Java 프로세스를 시작하지 못했습니다.");
        using var monitorStop = new CancellationTokenSource();
        var monitorTask = monitor is null
            ? Task.CompletedTask
            : Task.Run(() => RunMonitorAsync(monitor, new ServerRunContext(dir, config, resolved, process), monitorStop.Token));

        // 취소(Ctrl+C) 시에도 서버가 월드를 저장하고 스스로 끝날 때까지 기다린다.
        await process.WaitForExitAsync(CancellationToken.None);
        await monitorStop.CancelAsync();
        await monitorTask;
        return process.ExitCode;
    }

    private async Task RunMonitorAsync(Func<ServerRunContext, CancellationToken, Task> monitor, ServerRunContext context, CancellationToken ct)
    {
        try
        {
            await monitor(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log($"주의: 서버 모니터가 중단되었습니다 (서버는 계속 실행): {ex.Message}");
        }
    }

    public async Task<JavaInstallation> EnsureJavaAsync(
        int requiredMajor, bool allowDownload, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var installed = await JavaLocator.FindAllAsync(paths.RuntimesDir, ct);
        var selected = JavaLocator.SelectFor(installed, requiredMajor);
        if (selected is not null)
        {
            log($"Java 사용: {selected.VersionText} ({selected.ExecutablePath})");
            return selected;
        }

        if (!allowDownload)
            throw new InvalidOperationException($"Java {requiredMajor} 이상을 찾을 수 없습니다. 설치하거나 --no-java-download 옵션을 빼고 실행하세요.");

        log($"Java {requiredMajor} 이상이 없어 Eclipse Temurin JRE {requiredMajor}을(를) 내려받습니다 → {paths.RuntimesDir}");
        var java = await new AdoptiumClient(http).InstallJreAsync(requiredMajor, paths.RuntimesDir, progress, ct);
        log($"Java 설치 완료: {java.VersionText}");
        return java;
    }

    public Task<string> GetLatestStableGameVersionAsync(string modLoader = ModLoaders.Fabric, CancellationToken ct = default) =>
        GetLoader(modLoader).GetLatestGameVersionAsync(ct);

    private async Task<string> EnsureConfiguredJavaAsync(
        ServerInstanceConfig config, bool allowDownload, IProgress<double>? progress, CancellationToken ct)
    {
        if (config.JavaPath is not null && File.Exists(config.JavaPath))
        {
            var probe = await JavaLocator.ProbeAsync(config.JavaPath, managed: false, ct);
            if (probe is not null && probe.MajorVersion >= config.JavaMajorVersion)
                return config.JavaPath;
        }

        var java = await EnsureJavaAsync(config.JavaMajorVersion, allowDownload, progress, ct);
        config.JavaPath = java.ExecutablePath;
        return java.ExecutablePath;
    }
}
