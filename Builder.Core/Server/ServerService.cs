using System.Diagnostics;
using Builder.Core.Hardware;
using Builder.Core.Java;
using Builder.Core.Minecraft;
using Builder.Core.Mods;
using Builder.Core.Presets;

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
    bool AllowUnstableMods = false);

/// <summary>서버 생성·재구성·실행. CLI와 이후 웹 서비스가 함께 쓴다.</summary>
public sealed class ServerService(
    HttpClient http, PresetCatalog catalog, ModCatalog modCatalog, BuilderPaths paths, Action<string> log)
{
    private readonly FabricMetaClient fabric = new(http);
    private readonly MojangClient mojang = new(http);

    // 모드 소스 우선순위. CurseForge는 추후 이 목록 뒤에 추가한다 (설계서 6장).
    private readonly ModInstaller mods = new(http, modCatalog, [new ModrinthSource(http)], log);

    public async Task<(ServerInstanceConfig Config, ModSyncReport? Mods)> CreateAsync(
        CreateServerRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(request.Directory);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException($"폴더가 비어 있지 않습니다: {dir}\n기존 서버의 설정을 바꾸려면 'configure'를 사용하세요.");
        if (!request.EulaAccepted)
            throw new InvalidOperationException($"Minecraft EULA({EulaFile.EulaUrl})에 동의해야 서버를 만들 수 있습니다.");

        // 다운로드 전에 입력 오류를 먼저 확인한다.
        var preset = catalog.Get(request.Preset);
        var resolved = PresetResolver.Resolve(preset, request.Overrides);

        var game = request.MinecraftVersion ?? await GetLatestStableGameVersionAsync(ct);
        var loaders = await fabric.GetLoaderVersionsAsync(game, ct);
        if (loaders.Count == 0)
            throw new InvalidOperationException($"Fabric이 Minecraft {game}을(를) 지원하지 않습니다. 'versions' 명령으로 목록을 확인하세요.");

        var loader = request.LoaderVersion ?? FabricMetaClient.PickLatestStable(loaders)!.Version;
        if (!loaders.Any(l => l.Version == loader))
            throw new InvalidOperationException($"Minecraft {game}용 Fabric 로더 {loader}이(가) 없습니다.");

        var installer = FabricMetaClient.PickLatestStable(await fabric.GetInstallerVersionsAsync(ct))?.Version
            ?? throw new InvalidOperationException("Fabric 인스톨러 버전을 조회하지 못했습니다.");

        var javaMajor = await mojang.GetRequiredJavaMajorAsync(game, ct);
        log($"Minecraft {game} / Fabric 로더 {loader} / 인스톨러 {installer} / 필요 Java {javaMajor}");

        var java = await EnsureJavaAsync(javaMajor, request.AllowJavaDownload, progress, ct);

        var createdDir = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        try
        {
            log("Fabric 서버 런처 다운로드 중...");
            await fabric.DownloadServerLauncherAsync(game, loader, installer, Path.Combine(dir, ServerLaunch.LauncherJar), progress, ct);

            EulaFile.WriteAccepted(dir);

            var config = new ServerInstanceConfig
            {
                MinecraftVersion = game,
                LoaderVersion = loader,
                InstallerVersion = installer,
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
                log("최적화 모드 확인 중 (Modrinth)...");
                modReport = await mods.SyncAsync(dir, config, preset.Mods, update: true, ct);
            }

            config.Save(dir);
            ServerLaunch.Apply(dir, resolved, java.ExecutablePath);
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
        ServerLaunch.Apply(dir, resolved, java);
        return (config, resolved, modReport);
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
        log($"최적화 모드 확인 중 (Modrinth, Minecraft {config.MinecraftVersion})...");
        var report = await mods.SyncAsync(dir, config, preset.Mods, update: true, ct);
        config.Save(dir);
        return (config, report);
    }

    /// <summary>서버를 현재 콘솔에 붙여 실행하고 종료 코드를 돌려준다. 콘솔 입력은 서버로 그대로 전달된다.</summary>
    public async Task<int> RunAsync(string serverDir, bool allowJavaDownload, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(serverDir);
        var config = ServerInstanceConfig.Load(dir);
        if (!EulaFile.IsAccepted(dir))
            throw new InvalidOperationException($"{EulaFile.FileName}에서 EULA 동의가 확인되지 않습니다.");

        var resolved = PresetResolver.Resolve(catalog.Get(config.Preset), config.Overrides);
        var java = await EnsureConfiguredJavaAsync(config, allowJavaDownload, null, ct);
        config.Save(dir);

        var psi = new ProcessStartInfo(java) { WorkingDirectory = dir, UseShellExecute = false };
        foreach (var argument in ServerLaunch.BuildArguments(resolved))
            psi.ArgumentList.Add(argument);

        // 실행을 막지는 않고, 지금 여유 RAM이 부족하면 다른 프로그램을 닫도록 안내만 한다.
        var availableGb = HardwareProbe.Detect().AvailableMemoryGb;
        var requiredGb = PresetRecommender.RequiredGbForHeap(resolved.HeapMb);
        if (availableGb < requiredGb)
            log($"주의: 현재 여유 RAM {availableGb:0.0}GB, 서버에 약 {requiredGb:0.0}GB 필요. " +
                $"브라우저 등 다른 프로그램을 닫아 {requiredGb - availableGb:0.0}GB를 더 확보하세요. (부족하면 버벅임·디스크 스와핑 발생)");

        log($"서버 시작: 프리셋 {resolved.Source.DisplayName}({resolved.Source.Id}), 힙 {resolved.HeapMb}MB");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Java 프로세스를 시작하지 못했습니다.");
        // 취소(Ctrl+C) 시에도 서버가 월드를 저장하고 스스로 끝날 때까지 기다린다.
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
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

    public async Task<string> GetLatestStableGameVersionAsync(CancellationToken ct = default) =>
        (await fabric.GetGameVersionsAsync(ct)).FirstOrDefault(v => v.Stable)?.Version
        ?? throw new InvalidOperationException("Fabric 메타 API에서 안정 버전을 찾지 못했습니다.");

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
