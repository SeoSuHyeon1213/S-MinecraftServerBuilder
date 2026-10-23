using System.Globalization;
using Builder.Storage.Collection;

namespace Builder.Storage;

public enum ServerRunState
{
    /// <summary>시작·종료 이벤트가 없다.</summary>
    Unknown,
    Running,
    Stopped,

    /// <summary>시작 이벤트 뒤 종료 이벤트 없이 기록이 끊겼다 (빌더 강제 종료, 정전 등).</summary>
    Interrupted,
}

/// <summary>
/// 구간 요약의 성능 판정. 기준은 설계서 1장 "20 TPS / 틱당 50ms 이하"와 4장 5번 "95백분위 MSPT가 50ms 초과 시 한 단계 낮추기 제안".
/// </summary>
public sealed record PerformanceVerdict(bool EnoughData, bool MsptOverBudget, bool TpsBelowTarget)
{
    public bool Healthy => EnoughData && !MsptOverBudget && !TpsBelowTarget;
}

/// <summary><c>mchb stats</c>가 보여 줄 내용. 형식은 CLI가 정하고, 여기서는 값만 모은다.</summary>
public sealed record MetricsReport(
    string DatabasePath,
    DateTimeOffset From,
    DateTimeOffset To,
    MetricsSummary Summary,
    PerformanceVerdict Verdict,
    ServerRunState State,
    MetricSample? Latest,
    IReadOnlyList<MetricSample> Recent,
    IReadOnlyList<ServerEvent> Events,
    bool RangeExceedsRawRetention);

public static class MetricsReporting
{
    /// <summary>틱당 목표 시간(ms). 20 TPS = 50ms.</summary>
    public const double MsptBudgetMs = 50;

    /// <summary>이 TPS 밑으로 평균이 떨어지면 느려졌다고 본다 (측정 오차를 감안해 20보다 조금 낮게 둔다).</summary>
    public const double TpsTarget = 19.5;

    /// <summary>판정에 필요한 최소 MSPT 샘플 수. 너무 적으면 서버 시작 직후 값에 좌우된다.</summary>
    public const int MinimumSamples = 10;

    /// <summary>시작 이벤트 뒤 이 시간 넘게 샘플이 없으면 기록이 끊긴 것으로 본다 (가장 긴 프리셋 주기 60초의 5배).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public static MetricsReport Build(MetricsStore store, DateTimeOffset now, TimeSpan period,
        int recentCount = 5, int eventCount = 10, RetentionPolicy? retention = null)
    {
        retention ??= RetentionPolicy.Default;
        var from = now - period;
        // to는 구간 끝을 포함하도록 1ms 뒤로 둔다 (조회는 [from, to)).
        var to = now.AddMilliseconds(1);

        var summary = store.Summarize(from, to);
        var latest = store.LatestSample();
        var lifecycle = store.LatestEvent(EventTypes.ServerStart, EventTypes.ServerStop);
        var events = store.Events(from, to);

        return new MetricsReport(
            store.DatabasePath, from, now, summary, Judge(summary),
            DetermineState(lifecycle, latest, now),
            latest,
            recentCount > 0 ? store.RecentSamples(recentCount) : [],
            events.Skip(Math.Max(0, events.Count - eventCount)).ToList(),
            period > retention.RawSamples);
    }

    public static PerformanceVerdict Judge(MetricsSummary summary)
    {
        // MSPT 샘플 수는 따로 세지 않으므로, 95백분위가 있고 전체 샘플이 충분한지로 본다.
        var enough = summary.SampleCount >= MinimumSamples && summary.MsptP95 is not null;
        return new PerformanceVerdict(
            enough,
            enough && summary.MsptP95 > MsptBudgetMs,
            enough && summary.TpsAvg < TpsTarget);
    }

    public static ServerRunState DetermineState(ServerEvent? lastLifecycle, MetricSample? latest, DateTimeOffset now)
    {
        if (lastLifecycle is null)
            return ServerRunState.Unknown;
        if (lastLifecycle.Type == EventTypes.ServerStop)
            return ServerRunState.Stopped;
        var lastSeen = latest is null || latest.Timestamp < lastLifecycle.Timestamp ? lastLifecycle.Timestamp : latest.Timestamp;
        return now - lastSeen > StaleAfter ? ServerRunState.Interrupted : ServerRunState.Running;
    }

    /// <summary>"90s", "30m", "6h", "7d" 형식의 기간. 단위가 없으면 분으로 본다.</summary>
    public static TimeSpan ParseDuration(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0)
            throw new ArgumentException("기간을 지정하세요 (예: 30m, 6h, 7d).");

        var unit = char.IsLetter(t[^1]) ? t[^1] : 'm';
        var number = char.IsLetter(t[^1]) ? t[..^1] : t;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new ArgumentException($"기간 형식이 올바르지 않습니다: {text} (예: 30m, 6h, 7d)");

        return unit switch
        {
            's' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new ArgumentException($"기간 단위는 s, m, h, d 중 하나여야 합니다: {text}"),
        };
    }
}
