using Builder.Storage.Collection;

namespace Builder.Storage.Tests;

public class MetricsReportTests : StoreTestBase
{
    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("30", 1800)]
    [InlineData("1.5h", 5400)]
    [InlineData(" 2D ", 172800)]
    public void ParsesDurations(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), MetricsReporting.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("3w")]
    public void RejectsInvalidDurations(string text) =>
        Assert.Throws<ArgumentException>(() => MetricsReporting.ParseDuration(text));

    private static MetricsSummary Summary(int count, double? p95, double? tps) =>
        MetricsSummary.Empty with { SampleCount = count, MsptP95 = p95, TpsAvg = tps };

    [Fact]
    public void JudgesAgainstDesignTargets()
    {
        Assert.True(MetricsReporting.Judge(Summary(10, 50, 20)).Healthy);

        var slow = MetricsReporting.Judge(Summary(10, 50.1, 19.4));
        Assert.True(slow.MsptOverBudget);
        Assert.True(slow.TpsBelowTarget);
        Assert.False(slow.Healthy);

        // 샘플이 적거나 RCON 없이 프로세스 지표만 있으면 판정하지 않는다.
        Assert.False(MetricsReporting.Judge(Summary(9, 80, 10)).EnoughData);
        Assert.False(MetricsReporting.Judge(Summary(100, null, null)).EnoughData);
        Assert.False(MetricsReporting.Judge(Summary(9, 80, 10)).MsptOverBudget);
    }

    [Fact]
    public void DeterminesRunState()
    {
        var start = new ServerEvent(T0, EventTypes.ServerStart);
        Assert.Equal(ServerRunState.Unknown, MetricsReporting.DetermineState(null, null, T0));
        Assert.Equal(ServerRunState.Stopped,
            MetricsReporting.DetermineState(new ServerEvent(T0, EventTypes.ServerStop), null, T0.AddHours(1)));
        Assert.Equal(ServerRunState.Running,
            MetricsReporting.DetermineState(start, Sample(T0.AddMinutes(10), 1), T0.AddMinutes(14)));
        Assert.Equal(ServerRunState.Interrupted,
            MetricsReporting.DetermineState(start, Sample(T0.AddMinutes(10), 1), T0.AddMinutes(16)));
        // 이전 실행의 샘플은 무시하고 시작 시각부터 센다.
        Assert.Equal(ServerRunState.Running,
            MetricsReporting.DetermineState(start, Sample(T0.AddHours(-3), 1), T0.AddMinutes(2)));
    }

    [Fact]
    public void LatestEventFiltersByType()
    {
        var store = OpenStore();
        Assert.Null(store.LatestEvent(EventTypes.ServerStart));
        store.WriteBatch([], [
            new ServerEvent(T0, EventTypes.ServerStart),
            new ServerEvent(T0.AddMinutes(1), EventTypes.ServerStop),
            new ServerEvent(T0.AddMinutes(2), EventTypes.PlayerJoin),
        ]);
        Assert.Equal(EventTypes.ServerStop, store.LatestEvent(EventTypes.ServerStart, EventTypes.ServerStop)!.Type);
        Assert.Equal(T0, store.LatestEvent(EventTypes.ServerStart)!.Timestamp);
        Assert.Null(store.LatestEvent());
    }

    [Fact]
    public void BuildsReportForPeriod()
    {
        var store = OpenStore();
        var now = T0.AddHours(2);
        store.WriteBatch(
            [
                Sample(T0, 999) with { RssMb = 9999 }, // 구간(최근 1시간) 밖
                .. Enumerable.Range(0, 12).Select(i => Sample(now.AddMinutes(-11 + i), 60 + i) with { RssMb = 1000 + i, CpuPercent = 10 }),
            ],
            [
                new ServerEvent(T0, EventTypes.ServerStart),
                .. Enumerable.Range(0, 5).Select(i => new ServerEvent(now.AddMinutes(-5 + i), EventTypes.PlayerJoin, $"{i}")),
            ]);

        var report = MetricsReporting.Build(store, now, TimeSpan.FromHours(1), recentCount: 3, eventCount: 2);

        Assert.Equal(store.DatabasePath, report.DatabasePath);
        Assert.Equal(12, report.Summary.SampleCount);
        Assert.Equal(1011, report.Summary.RssMaxMb);
        Assert.Equal(10, report.Summary.CpuAvg);
        Assert.True(report.Verdict.MsptOverBudget);
        Assert.Equal(ServerRunState.Running, report.State);
        Assert.Equal(now, report.Latest!.Timestamp);
        Assert.Equal([69.0, 70.0, 71.0], report.Recent.Select(r => r.MsptAvg!.Value));
        // 구간 안 이벤트 중 최근 2개 (시작 이벤트는 구간 밖)
        Assert.Equal(["3", "4"], report.Events.Select(e => e.Payload));
        Assert.False(report.RangeExceedsRawRetention);

        var empty = MetricsReporting.Build(store, now, TimeSpan.FromDays(3), recentCount: 0, eventCount: 0);
        Assert.True(empty.RangeExceedsRawRetention);
        Assert.Empty(empty.Recent);
        Assert.Empty(empty.Events);
    }
}
