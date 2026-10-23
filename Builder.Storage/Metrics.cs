namespace Builder.Storage;

/// <summary>
/// 한 시점의 서버 지표. 수집 경로(RCON·프로세스·FabricBridge)마다 얻을 수 있는 값이 달라 모든 값은 선택 항목이다.
/// </summary>
public sealed record MetricSample(DateTimeOffset Timestamp)
{
    public double? Tps { get; init; }
    public double? MsptAvg { get; init; }
    public double? MsptP95 { get; init; }
    public long? HeapUsedMb { get; init; }
    public long? RssMb { get; init; }
    public double? CpuPercent { get; init; }
    public int? Players { get; init; }
}

/// <summary>접속·퇴장·경고·재시작 같은 개별 사건. <see cref="Payload"/>는 형식이 정해지지 않은 부가 정보(JSON 등)다.</summary>
public sealed record ServerEvent(DateTimeOffset Timestamp, string Type, string? Payload = null);

/// <summary>1분 단위 집계 지표.</summary>
public sealed record MinuteAggregate(
    DateTimeOffset Minute,
    int SampleCount,
    double? TpsAvg,
    double? TpsMin,
    double? MsptAvg,
    double? MsptMax,
    long? HeapUsedMaxMb,
    long? RssMaxMb,
    double? CpuAvg,
    int? PlayersMax);

/// <summary>기간 요약. 적응형 단계 조정(설계서 4장 5번)의 MSPT 95백분위 판단에 쓴다.</summary>
public sealed record MetricsSummary(
    int SampleCount,
    double? TpsAvg,
    double? TpsMin,
    double? MsptAvg,
    double? MsptP95,
    long? HeapUsedMaxMb,
    int? PlayersMax,
    long? RssMaxMb,
    double? CpuAvg)
{
    public static MetricsSummary Empty { get; } = new(0, null, null, null, null, null, null, null, null);
}

/// <summary>
/// 보존 기간. 원본 샘플은 짧게, 1분 집계는 길게 두어 저사양 PC의 디스크 사용량을 제한한다.
/// </summary>
public sealed record RetentionPolicy
{
    public TimeSpan RawSamples { get; init; } = TimeSpan.FromHours(48);
    public TimeSpan MinuteAggregates { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan Events { get; init; } = TimeSpan.FromDays(30);

    /// <summary>늦게 기록되는 배치를 놓치지 않도록, 집계는 이 시간만큼 지난 분까지만 만든다.</summary>
    public TimeSpan AggregationDelay { get; init; } = TimeSpan.FromMinutes(1);

    public static RetentionPolicy Default { get; } = new();
}
