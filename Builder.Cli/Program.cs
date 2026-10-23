using System.Text;
using Builder.Cli;
using Builder.Core;
using Builder.Core.Hardware;
using Builder.Core.Http;
using Builder.Core.Java;
using Builder.Core.Loaders;
using Builder.Core.Minecraft;
using Builder.Core.Mods;
using Builder.Core.Presets;
using Builder.Core.Server;
using Builder.Storage.Collection;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var cli = new CommandLine(args);
    if (cli.Flag("help"))
        return Help();

    var paths = BuilderPaths.Default();
    var catalog = PresetCatalog.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "Presets"));
    using var http = BuilderHttp.CreateClient();
    using var cts = new CancellationTokenSource();
    var modCatalog = ModCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Mods", "catalog.json"));
    var service = new ServerService(http, catalog, modCatalog, paths, Console.WriteLine);

    return cli.Command switch
    {
        "hardware" or "detect" => await Hardware(paths, catalog),
        "presets" => Presets(catalog, modCatalog),
        "versions" => await Versions(new FabricMetaClient(http), new NeoForgeServerLoader(http, Console.WriteLine),
            ModLoaders.Normalize(cli.Option("modloader") ?? ModLoaders.Fabric), cli.Flag("all")),
        "create" => await Create(cli, service, catalog, cts.Token),
        "configure" => await Configure(cli, service),
        "start" => await Start(cli, service),
        "mods" => await Mods(cli, service, modCatalog, catalog),
        "switch-loader" or "loader" => await SwitchLoader(cli, service),
        "help" => Help(),
        _ => throw new ArgumentException($"알 수 없는 명령: {cli.Command} ('help'로 목록 확인)"),
    };
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                               or HttpRequestException or InvalidDataException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"오류: {ex.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        Minecraft Hybrid Server Builder (mchb) — 저사양 PC용 Fabric / NeoForge 서버 빌더

        명령:
          hardware                       하드웨어·Java 확인과 프리셋 추천
          presets                        경량화 프리셋 목록
          versions [--modloader <로더>] [--all]
                                         로더가 지원하는 Minecraft 버전 (기본: Fabric, 안정 버전 15개)
          create <폴더> [옵션]            새 서버 생성
              --modloader <로더>         fabric|neoforge 또는 페브릭|네오포지
                                         (생략 시 대화형이면 선택 질문, 아니면 fabric)
              --mc <버전>                Minecraft 버전 (기본: 해당 로더의 최신 안정 버전)
              --loader <버전>            로더 버전 (기본: 최신 안정 버전. NeoForge 예: 26.3.0.90)
              --preset <이름>            normal|medium|low|minimum 또는 일반|중간|저|최저 (기본: 하드웨어 추천)
              --set <키=값>              오버라이드, 여러 번 사용 가능 (예: server.view-distance=8, jvm.heapMb=3584)
              --accept-eula              Minecraft EULA(https://aka.ms/MinecraftEULA) 동의
              --no-java-download         필요한 Java가 없어도 자동 설치하지 않음
              --no-mods                  최적화 모드를 자동 설치하지 않음
              --allow-unstable-mods      정식 버전이 없는 모드는 베타·알파 버전 설치 허용
          configure <폴더> [--preset <이름>] [--set <키=값>]... [--unset <키>]...
                                         프리셋·오버라이드 변경 후 server.properties와 시작 스크립트 재생성
                                         (--unset은 오버라이드만 지움. 프리셋에 없는 키는 파일의 현재 값이 유지됨)
          mods <폴더> [--update] [--allow-unstable-mods[=false]]
                                         설치된 최적화 모드 확인. --update: Modrinth에서 최신 호환 버전으로 갱신
                                         (아직 버전이 없어 건너뛴 모드도 이때 다시 찾음)
          switch-loader <폴더> <로더> [--loader <버전>] [--confirm]
                                         같은 Minecraft 버전에서 Fabric ↔ NeoForge 전환
                                         (빌더 모드는 새 로더용으로 재설치, 사용자 jar 중 이전 로더 전용은
                                          mods-disabled/<로더>/ 로 옮김. 월드가 있으면 백업 후 --confirm 필요)
          start <폴더> [--no-metrics]     서버 실행 (콘솔 명령 입력 가능, 종료는 'stop' 또는 Ctrl+C)
                                         실행 중 RCON으로 TPS·MSPT·접속자, 프로세스 메모리·CPU를
                                         builder-data/metrics.db에 기록 (주기는 프리셋별, --set metrics.intervalSeconds=초,
                                         0이면 끔. --no-metrics는 이번 실행만 끔)
        """);
    return 0;
}

static async Task<int> Hardware(BuilderPaths paths, PresetCatalog catalog)
{
    var hw = HardwareProbe.Detect();
    Console.WriteLine($"OS          : {hw.OsDescription}");
    Console.WriteLine($"논리 프로세서: {hw.LogicalProcessors}");
    Console.WriteLine($"RAM         : 전체 {hw.TotalMemoryGb:0.0}GB / 여유 {hw.AvailableMemoryGb:0.0}GB");

    PrintRecommendation(PresetRecommender.Recommend(hw), catalog);

    Console.WriteLine();
    Console.WriteLine("Java 런타임:");
    var javas = await JavaLocator.FindAllAsync(paths.RuntimesDir);
    if (javas.Count == 0)
        Console.WriteLine("  (없음 — create 시 필요한 버전을 자동으로 내려받습니다)");
    foreach (var java in javas)
        Console.WriteLine($"  Java {java.MajorVersion,-3} {(java.Managed ? "[빌더 관리] " : "")}{java.ExecutablePath}");
    return 0;
}

static void PrintRecommendation(PresetRecommendation recommendation, PresetCatalog catalog)
{
    var preset = catalog.Get(recommendation.PresetId);
    Console.WriteLine($"추천 프리셋  : {preset.DisplayName} ({preset.Id}) — {recommendation.Reason}");
    Console.WriteLine($"필요 RAM    : 약 {recommendation.RequiredGb:0.0}GB (현재 여유 {recommendation.AvailableGb:0.0}GB)");
    if (recommendation.BelowMinimum)
        Console.WriteLine("경고        : 전체 RAM이 최저 단계 기준에도 부족합니다. 서버가 느리거나 불안정할 수 있습니다.");
    else if (recommendation.ShortfallGb > 0)
        Console.WriteLine($"안내        : 서버 실행 전 브라우저 등 다른 프로그램을 닫아 약 {recommendation.ShortfallGb:0.0}GB를 더 확보하세요.");
}

static int Presets(PresetCatalog catalog, ModCatalog modCatalog)
{
    foreach (var p in catalog.All)
    {
        var props = p.ServerProperties;
        Console.WriteLine($"{p.DisplayName} ({p.Id}) — {p.Description}");
        Console.WriteLine($"  힙 {p.HeapMb / 1024.0:0.#}GB, 권장 인원 {p.RecommendedPlayers}, " +
                          $"view {props.GetValueOrDefault("view-distance")}, simulation {props.GetValueOrDefault("simulation-distance")}, " +
                          $"sync-chunk-writes {props.GetValueOrDefault("sync-chunk-writes")}, 청크 사전 생성 {p.ChunkPregeneration}" +
                          (p.RequiresAutoBackup ? ", 자동 백업 필수" : ""));
        Console.WriteLine($"  모드: {string.Join(", ", p.Mods.Select(id => modCatalog.Get(id).Name))}");
    }
    return 0;
}

static async Task<int> Versions(FabricMetaClient fabric, NeoForgeServerLoader neoForge, string loader, bool all)
{
    if (loader == ModLoaders.NeoForge)
    {
        var summary = NeoForgeVersions.Summarize(await neoForge.GetAllVersionsAsync());
        foreach (var (game, build, stable) in all ? summary : summary.Where(v => v.Stable).Take(15).ToList())
            Console.WriteLine($"{game,-10} NeoForge {build}{(stable ? "" : " (베타만 있음)")}");
        return 0;
    }

    var versions = await fabric.GetGameVersionsAsync();
    var shown = all ? versions : versions.Where(v => v.Stable).Take(15).ToList();
    foreach (var v in shown)
        Console.WriteLine(v.Stable ? v.Version : $"{v.Version} (불안정)");
    return 0;
}

static async Task<int> Create(CommandLine cli, ServerService service, PresetCatalog catalog, CancellationToken ct)
{
    var dir = cli.RequirePositional(0, "서버 폴더");
    var overrides = cli.KeyValues("set");
    foreach (var key in overrides.Keys)
        PresetResolver.ValidateKey(key);

    var presetId = cli.Option("preset");
    if (presetId is null)
    {
        var recommendation = PresetRecommender.Recommend(HardwareProbe.Detect());
        presetId = recommendation.PresetId;
        Console.WriteLine("프리셋 자동 선택:");
        PrintRecommendation(recommendation, catalog);
    }
    catalog.Get(presetId);

    var modLoader = ModLoaders.Normalize(cli.Option("modloader") ?? AskModLoader());
    var eula = cli.Flag("accept-eula") || AskEula();
    var (config, modReport) = await service.CreateAsync(
        new CreateServerRequest(dir, cli.Option("mc"), cli.Option("loader"), presetId, overrides, eula,
            !cli.Flag("no-java-download"), !cli.Flag("no-mods"), cli.Flag("allow-unstable-mods"), modLoader),
        new ConsoleProgress(), ct);
    if (modReport is not null)
        PrintModReport(modReport);

    var full = Path.GetFullPath(dir);
    Console.WriteLine();
    Console.WriteLine($"서버 생성 완료: {full}");
    Console.WriteLine($"  Minecraft {config.MinecraftVersion}, {ModLoaders.DisplayName(config.Loader)} {config.LoaderVersion}, 프리셋 {config.Preset}");
    Console.WriteLine($"  실행: mchb start \"{full}\"  또는  {(OperatingSystem.IsWindows() ? "start.bat" : "./start.sh")}");
    Console.WriteLine("  첫 실행 시 Minecraft 서버 파일을 내려받고 월드를 생성하므로 시간이 걸립니다.");
    return 0;
}

static async Task<int> Configure(CommandLine cli, ServerService service)
{
    var dir = cli.RequirePositional(0, "서버 폴더");
    var (config, resolved, modReport) = await service.ConfigureAsync(
        dir, cli.Option("preset"), cli.KeyValues("set"), cli.Options("unset").ToList(),
        !cli.Flag("no-java-download"), new ConsoleProgress());

    Console.WriteLine($"설정 반영: {ModLoaders.DisplayName(config.Loader)} {config.LoaderVersion}, 프리셋 {resolved.Source.DisplayName} ({config.Preset}), 힙 {resolved.HeapMb}MB");
    foreach (var (key, value) in config.Overrides)
        Console.WriteLine($"  오버라이드 {key}={value}");
    if (modReport is not null)
        PrintModReport(modReport);
    Console.WriteLine("JVM 힙·모드 변경은 서버를 재시작해야 적용됩니다.");
    return 0;
}

static async Task<int> Start(CommandLine cli, ServerService service)
{
    var dir = cli.RequirePositional(0, "서버 폴더");
    // Ctrl+C는 같은 콘솔의 Java 프로세스에도 전달된다. 빌더는 종료하지 않고 서버가 저장 후 끝나기를 기다린다.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("종료 신호를 받았습니다. 서버가 월드를 저장하고 종료할 때까지 기다립니다...");
    };
    // 수집기는 서버와 같은 콘솔에 출력하므로 상태가 바뀔 때만 한 줄씩 남긴다.
    Func<ServerRunContext, CancellationToken, Task>? monitor = cli.Flag("no-metrics")
        ? null
        : (context, token) => context.Preset.MetricsEnabled
            ? MetricsCollector.MonitorAsync(context, line => Console.WriteLine($"[mchb] {line}"), token)
            : Task.CompletedTask;
    var exitCode = await service.RunAsync(dir, !cli.Flag("no-java-download"), monitor);
    Console.WriteLine($"서버 종료 (코드 {exitCode})");
    return exitCode;
}

static async Task<int> Mods(CommandLine cli, ServerService service, ModCatalog modCatalog, PresetCatalog catalog)
{
    var dir = cli.RequirePositional(0, "서버 폴더");
    ServerInstanceConfig config;
    if (cli.Flag("update"))
    {
        var unstable = cli.Option("allow-unstable-mods") is null ? (bool?)null : cli.Flag("allow-unstable-mods");
        (config, var report) = await service.UpdateModsAsync(dir, unstable);
        PrintModReport(report);
        Console.WriteLine();
    }
    else
    {
        config = ServerInstanceConfig.Load(Path.GetFullPath(dir));
    }

    Console.WriteLine($"Minecraft {config.MinecraftVersion}, {ModLoaders.DisplayName(config.Loader)} {config.LoaderVersion}, " +
                      $"프리셋 {config.Preset}, 자동 관리 {(config.ManageMods ? "켜짐" : "꺼짐")}, " +
                      $"알파·베타 허용 {(config.AllowUnstableMods ? "예" : "아니요")}");
    if (config.Mods.Count == 0)
        Console.WriteLine("  빌더가 설치한 모드 없음");
    foreach (var mod in config.Mods)
        Console.WriteLine($"  {mod.Name,-16} {mod.VersionNumber,-28} {(mod.CatalogId is null ? "[의존성] " : "")}{mod.FileName}");

    var full = Path.GetFullPath(dir);
    var modCount = ModdedHeapAdvisor.CountModJars(full);
    var heapMb = PresetResolver.Resolve(catalog.Get(config.Preset), config.Overrides).HeapMb;
    var advisedMb = ModdedHeapAdvisor.RecommendedHeapMb(config.Loader, modCount);
    Console.WriteLine($"mods 폴더 jar {modCount}개 (사용자 모드 포함), 현재 힙 {heapMb}MB, 대략적인 권장 힙 {advisedMb}MB" +
                      (heapMb < advisedMb ? $" → 'configure --set {PresetResolver.HeapKey}={advisedMb}' 권장" : ""));
    return 0;
}

static async Task<int> SwitchLoader(CommandLine cli, ServerService service)
{
    var dir = cli.RequirePositional(0, "서버 폴더");
    var target = cli.Positional.Count > 1 ? cli.Positional[1]
        : cli.Option("modloader") ?? throw new ArgumentException("바꿀 로더를 지정하세요 (fabric 또는 neoforge).");

    var report = await service.SwitchLoaderAsync(dir, target, cli.Option("loader"), cli.Flag("confirm"),
        !cli.Flag("no-java-download"), new ConsoleProgress());

    Console.WriteLine();
    Console.WriteLine($"로더 전환 완료: {ModLoaders.DisplayName(report.FromLoader)} → {ModLoaders.DisplayName(report.ToLoader)} {report.LoaderVersion}");
    foreach (var name in report.RemovedManagedMods)
        Console.WriteLine($"  [제거] {name} (이전 로더용 빌더 모드)");
    foreach (var name in report.DisabledUserMods)
        Console.WriteLine($"  [보관] {name} → {UserModShelf.DisabledDirName}/{report.FromLoader}/");
    foreach (var name in report.RestoredUserMods)
        Console.WriteLine($"  [복원] {name}");
    if (report.Mods is not null)
        PrintModReport(report.Mods);
    Console.WriteLine("새 로더용 모드는 mods 폴더에 직접 넣으세요. 이전 로더로 돌아가면 보관한 jar는 자동으로 되돌아옵니다.");
    return 0;
}

static void PrintModReport(ModSyncReport report)
{
    Print("설치", report.Installed);
    Print("업데이트", report.Updated);
    Print("유지", report.Kept);
    Print("제거", report.Removed);
    Print("건너뜀", report.Skipped);

    static void Print(string label, List<ModChange> changes)
    {
        foreach (var change in changes)
            Console.WriteLine($"  [{label}] {change.Name} — {change.Detail}");
    }
}

/// <summary>대화형 콘솔이면 로더를 묻는다. 입력이 리디렉션된 경우(스크립트)에는 Fabric.</summary>
static string AskModLoader()
{
    if (Console.IsInputRedirected)
        return ModLoaders.Fabric;
    Console.Write("모드 로더를 고르세요 — 1) Fabric (기본, 가벼움)  2) NeoForge : ");
    var answer = Console.ReadLine()?.Trim();
    return answer is "2" ? ModLoaders.NeoForge
        : string.IsNullOrEmpty(answer) || answer == "1" ? ModLoaders.Fabric
        : answer;
}

static bool AskEula()
{
    if (Console.IsInputRedirected)
        return false;
    Console.Write($"Minecraft EULA({EulaFile.EulaUrl})에 동의합니까? [y/N] ");
    var answer = Console.ReadLine()?.Trim();
    return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

/// <summary>다운로드 진행률을 한 줄에 표시한다 (5% 단위).</summary>
sealed class ConsoleProgress : IProgress<double>
{
    private int lastStep = -1;

    public void Report(double value)
    {
        var step = (int)(value * 20);
        if (step == lastStep)
            return;
        lastStep = step;
        Console.Write($"\r  다운로드 {step * 5,3}%");
        if (step >= 20)
        {
            Console.WriteLine();
            lastStep = -1;
        }
    }
}
