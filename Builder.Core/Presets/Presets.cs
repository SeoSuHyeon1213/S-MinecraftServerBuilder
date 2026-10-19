using System.Text.Json;
using Builder.Core.Hardware;
using Builder.Core.Http;
using Builder.Core.Loaders;

namespace Builder.Core.Presets;

/// <summary>Presets/*.json 한 파일의 내용.</summary>
public sealed class PresetDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public int Order { get; init; }
    public string Description { get; init; } = "";
    public string RecommendedPlayers { get; init; } = "";
    public required int HeapMb { get; init; }
    public List<string> JvmFlags { get; init; } = [];
    public Dictionary<string, string> ServerProperties { get; init; } = [];

    /// <summary>자동 설치할 최적화 모드 (Mods/catalog.json의 id).</summary>
    public List<string> Mods { get; init; } = [];

    /// <summary>optional / recommended / required / requiredWithWorldBorder (2차 단계에서 Chunky 연동 예정)</summary>
    public string ChunkPregeneration { get; init; } = "optional";

    /// <summary>sync-chunk-writes=false 단계에서는 자동 백업을 강제한다 (설계서 4장 안전 장치).</summary>
    public bool RequiresAutoBackup { get; init; }
}

public sealed class PresetCatalog
{
    private readonly List<PresetDefinition> presets;

    public PresetCatalog(IEnumerable<PresetDefinition> presets)
    {
        this.presets = presets.OrderBy(p => p.Order).ToList();
    }

    public IReadOnlyList<PresetDefinition> All => presets;

    public static PresetCatalog LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"프리셋 폴더가 없습니다: {directory}");

        var list = new List<PresetDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var preset = JsonSerializer.Deserialize<PresetDefinition>(File.ReadAllText(file), BuilderHttp.Json)
                ?? throw new InvalidDataException($"프리셋 파일을 읽을 수 없습니다: {file}");
            list.Add(preset);
        }
        return new PresetCatalog(list);
    }

    /// <summary>id(normal) 또는 표시 이름(일반)으로 찾는다.</summary>
    public PresetDefinition Get(string idOrName) =>
        presets.FirstOrDefault(p =>
            p.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) ||
            p.DisplayName.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"알 수 없는 프리셋: {idOrName} (사용 가능: {string.Join(", ", presets.Select(p => $"{p.Id}/{p.DisplayName}"))})");
}

/// <summary>프리셋 기본값에 사용자 오버라이드를 병합한 최종 설정.</summary>
public sealed record ResolvedPreset(
    PresetDefinition Source,
    int HeapMb,
    IReadOnlyList<string> JvmFlags,
    IReadOnlyDictionary<string, string> ServerProperties);

public static class PresetResolver
{
    public const string ServerPrefix = "server.";
    public const string HeapKey = "jvm.heapMb";
    public const int MinimumHeapMb = 1024;

    public static ResolvedPreset Resolve(PresetDefinition preset, IReadOnlyDictionary<string, string>? overrides)
    {
        var properties = new Dictionary<string, string>(preset.ServerProperties, StringComparer.Ordinal);
        var heapMb = preset.HeapMb;

        foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
        {
            ValidateKey(key);
            if (key.StartsWith(ServerPrefix, StringComparison.Ordinal))
            {
                properties[key[ServerPrefix.Length..]] = value;
            }
            else
            {
                if (!int.TryParse(value, out heapMb) || heapMb < MinimumHeapMb)
                    throw new ArgumentException($"{HeapKey} 값은 {MinimumHeapMb} 이상의 정수(MB)여야 합니다: {value}");
            }
        }

        return new ResolvedPreset(preset, heapMb, preset.JvmFlags, properties);
    }

    /// <summary>오버라이드 키 형식을 확인한다. 다운로드 전에 오류를 알리기 위해 따로 호출할 수 있다.</summary>
    public static void ValidateKey(string key)
    {
        if (key.StartsWith(ServerPrefix, StringComparison.Ordinal) && key.Length > ServerPrefix.Length)
            return;
        if (key == HeapKey)
            return;
        throw new ArgumentException($"지원하지 않는 오버라이드 키: {key} (사용 가능: server.<속성 이름>, {HeapKey})");
    }
}

public sealed record PresetRecommendation(
    string PresetId,
    string Reason,
    double UsableGb,
    double RequiredGb,
    double AvailableGb)
{
    /// <summary>지금 바로 서버를 실행하려면 더 확보해야 하는 RAM(GB). 0이면 충분하다.</summary>
    public double ShortfallGb => Math.Max(0, RequiredGb - AvailableGb);

    /// <summary>다른 프로그램을 모두 닫아도 최저 단계 기준에 못 미친다.</summary>
    public bool BelowMinimum => UsableGb < PresetRecommender.MinimumRequiredGb;
}

public static class PresetRecommender
{
    private const double Gb = 1024d * 1024 * 1024;

    /// <summary>힙 외에 JVM 자체(메타스페이스, 스레드 스택, 네이티브 메모리)가 쓰는 대략적인 RAM.</summary>
    public const double JvmOverheadGb = 1.5;

    /// <summary>서버 프로세스 필요 RAM = 힙 + JVM 오버헤드 (일반은 여유를 더 둠). 위에서부터 조건을 만족하는 첫 단계를 고른다.</summary>
    private static readonly (string Id, double RequiredGb, int MinCores)[] Tiers =
    [
        ("normal", 8.0, 6),
        ("medium", 6.5, 4),
        ("low", 5.5, 0),
        ("minimum", 4.5, 0),
    ];

    public static double MinimumRequiredGb => Tiers[^1].RequiredGb;

    /// <summary>OS 자체가 쓰는 몫. Windows 11은 유휴 상태에서도 약 3GB를 사용한다.</summary>
    public static double DefaultOsReserveGb => OperatingSystem.IsWindows() ? 3.0 : 1.5;

    /// <summary>
    /// 전체 RAM에서 OS 몫을 뺀 "서버용 RAM"과 논리 프로세서 수로 단계를 추천한다.
    /// 측정 순간의 여유 RAM은 브라우저 등 잠시 열린 프로그램에 좌우되므로 추천 기준으로 쓰지 않고,
    /// 지금 실행하려면 얼마를 더 확보해야 하는지(ShortfallGb) 안내하는 데만 쓴다.
    /// </summary>
    public static PresetRecommendation Recommend(HardwareInfo hardware, double? osReserveGb = null)
    {
        var reserve = osReserveGb ?? DefaultOsReserveGb;
        var totalGb = hardware.TotalMemoryBytes / Gb;
        var usableGb = totalGb - reserve;
        var cores = hardware.LogicalProcessors;

        var tier = Tiers.FirstOrDefault(t => usableGb >= t.RequiredGb && cores >= t.MinCores);
        if (tier.Id is null)
            tier = Tiers[^1];

        var reason = $"전체 RAM {totalGb:0.0}GB - OS 몫 {reserve:0.#}GB = 서버용 {usableGb:0.0}GB, 논리 프로세서 {cores}개";
        return new PresetRecommendation(tier.Id, reason, usableGb, tier.RequiredGb, hardware.AvailableMemoryBytes / Gb);
    }

    /// <summary>실제 힙 설정(오버라이드 포함) 기준으로 서버가 쓰는 대략적인 RAM.</summary>
    public static double RequiredGbForHeap(int heapMb) => heapMb / 1024d + JvmOverheadGb;
}

/// <summary>
/// 모드 수에 따른 대략적인 권장 힙. 실측값이 아니라 흔히 쓰는 어림값(로더 기본 + 모드당 64MB)이며,
/// 경고 표시에만 쓰고 설정을 자동으로 바꾸지 않는다. 정확한 값은 spark(/spark heapsummary)로 확인한다.
/// </summary>
public static class ModdedHeapAdvisor
{
    public const int PerModMb = 64;

    /// <summary>NeoForge는 시작 시 로드하는 라이브러리·이벤트 버스가 더 커서 기본값을 높게 잡는다.</summary>
    public static int BaseMb(string loader) => loader == ModLoaders.NeoForge ? 2560 : 2048;

    /// <summary>256MB 단위로 올림한 권장 힙(MB).</summary>
    public static int RecommendedHeapMb(string loader, int modCount)
    {
        var mb = BaseMb(loader) + PerModMb * Math.Max(0, modCount);
        return (mb + 255) / 256 * 256;
    }

    public static int CountModJars(string serverDir)
    {
        var modsDir = Path.Combine(serverDir, "mods");
        return Directory.Exists(modsDir) ? Directory.EnumerateFiles(modsDir, "*.jar").Count() : 0;
    }
}
