using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Builder.Storage.Tests;

public abstract class StoreTestBase : IDisposable
{
    protected readonly string Dir = Directory.CreateTempSubdirectory("mchb-metrics-").FullName;
    protected static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        // 연결 풀이 파일을 잡고 있으면 Windows에서 폴더를 지울 수 없다.
        SqliteConnection.ClearAllPools();
        Directory.Delete(Dir, recursive: true);
    }

    protected MetricsStore OpenStore() => MetricsStore.OpenForServer(Dir);

    protected static MetricSample Sample(DateTimeOffset ts, double mspt, double tps = 20, int players = 1) =>
        new(ts) { Tps = tps, MsptAvg = mspt, HeapUsedMb = 1024, Players = players };
}

public class MetricsStoreTests : StoreTestBase
{
    [Fact]
    public void CreatesDatabaseInServerDataFolderWithWal()
    {
        var store = OpenStore();
        Assert.Equal(Path.Combine(Dir, "builder-data", "metrics.db"), store.DatabasePath);
        Assert.True(File.Exists(store.DatabasePath));

        using var conn = new SqliteConnection($"Data Source={store.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA user_version;";
        Assert.Equal((long)MetricsStore.SchemaVersion, cmd.ExecuteScalar());
    }

    [Fact]
    public void ReopeningKeepsData()
    {
        OpenStore().WriteBatch([Sample(T0, 10)], [new ServerEvent(T0, "start")]);
        var store = OpenStore();
        Assert.Single(store.RecentSamples(10));
        Assert.Single(store.Events(T0, T0.AddSeconds(1)));
    }

    [Fact]
    public void RefusesNewerSchema()
    {
        var store = OpenStore();
        using (var conn = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {MetricsStore.SchemaVersion + 1};";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => OpenStore());
    }

    [Fact]
    public void RoundTripsSamplesWithMissingValues()
    {
        var store = OpenStore();
        var full = new MetricSample(T0)
        {
            Tps = 19.5, MsptAvg = 42.25, MsptP95 = 60, HeapUsedMb = 2048, RssMb = 3000, CpuPercent = 35.5, Players = 3,
        };
        var partial = new MetricSample(T0.AddSeconds(1)) { RssMb = 3100 };
        store.WriteBatch([full, partial], []);

        var read = store.RecentSamples(10);
        Assert.Equal([full, partial], read);
    }

    [Fact]
    public void RecentSamplesReturnsNewestInChronologicalOrder()
    {
        var store = OpenStore();
        store.WriteBatch(Enumerable.Range(0, 5).Select(i => Sample(T0.AddSeconds(i), i)).ToList(), []);
        Assert.Equal([3.0, 4.0], store.RecentSamples(2).Select(s => s.MsptAvg!.Value));
    }

    [Fact]
    public void FiltersEventsByType()
    {
        var store = OpenStore();
        store.WriteBatch([], [
            new ServerEvent(T0, "join", """{"player":"a"}"""),
            new ServerEvent(T0.AddSeconds(1), "warn"),
            new ServerEvent(T0.AddSeconds(2), "join", """{"player":"b"}"""),
        ]);
        var joins = store.Events(T0, T0.AddMinutes(1), "join");
        Assert.Equal(["""{"player":"a"}""", """{"player":"b"}"""], joins.Select(e => e.Payload));
        Assert.Equal(3, store.Events(T0, T0.AddMinutes(1)).Count);
        Assert.Null(store.Events(T0, T0.AddMinutes(1), "warn")[0].Payload);
    }

    [Fact]
    public void SummarizesRangeWithP95()
    {
        var store = OpenStore();
        // MSPT 1..100: 최근접 순위 95백분위는 95
        var batch = Enumerable.Range(1, 100)
            .Select(i => Sample(T0.AddSeconds(i - 1), i, tps: i <= 50 ? 20 : 18, players: i % 7))
            .ToList();
        batch.Add(Sample(T0.AddHours(1), 999)); // 구간 밖
        store.WriteBatch(batch, []);

        var s = store.Summarize(T0, T0.AddMinutes(10));
        Assert.Equal(100, s.SampleCount);
        Assert.Equal(95, s.MsptP95);
        Assert.Equal(50.5, s.MsptAvg);
        Assert.Equal(19, s.TpsAvg);
        Assert.Equal(18, s.TpsMin);
        Assert.Equal(6, s.PlayersMax);
        Assert.Equal(1024, s.HeapUsedMaxMb);
    }

    [Fact]
    public void SummaryOfEmptyRangeHasNoValues()
    {
        var s = OpenStore().Summarize(T0, T0.AddMinutes(1));
        Assert.Equal(MetricsSummary.Empty, s);
    }

    [Theory]
    [InlineData(new double[] { 5 }, 0.95, 5)]
    [InlineData(new double[] { 1, 2, 3, 4 }, 0.5, 2)]
    [InlineData(new double[] { 1, 2, 3, 4 }, 0.95, 4)]
    [InlineData(new double[] { 1, 2, 3, 4 }, 0, 1)]
    public void PercentileUsesNearestRank(double[] sorted, double p, double expected) =>
        Assert.Equal(expected, MetricsStore.Percentile(sorted, p));
}

public class MetricsCompactionTests : StoreTestBase
{
    [Fact]
    public void AggregatesFinishedMinutesOnly()
    {
        var store = OpenStore();
        store.WriteBatch([
            Sample(T0, 10, tps: 20, players: 1),
            Sample(T0.AddSeconds(30), 30, tps: 18, players: 4) with { MsptP95 = 70 },
            Sample(T0.AddMinutes(1), 50),
            Sample(T0.AddMinutes(2).AddSeconds(5), 99), // 집계 지연 안쪽
        ], []);

        store.Compact(T0.AddMinutes(3));

        var minutes = store.MinuteAggregates(T0, T0.AddHours(1));
        Assert.Equal(2, minutes.Count);
        var first = minutes[0];
        Assert.Equal(T0, first.Minute);
        Assert.Equal(2, first.SampleCount);
        Assert.Equal(19, first.TpsAvg);
        Assert.Equal(18, first.TpsMin);
        Assert.Equal(20, first.MsptAvg);
        Assert.Equal(70, first.MsptMax); // mspt_p95가 있으면 그 값, 없으면 평균 MSPT의 최댓값
        Assert.Equal(4, first.PlayersMax);
        Assert.Equal(T0.AddMinutes(1), minutes[1].Minute);
    }

    [Fact]
    public void RepeatedCompactionDoesNotDuplicateOrReaggregate()
    {
        var store = OpenStore();
        store.WriteBatch([Sample(T0, 10), Sample(T0.AddMinutes(1), 20)], []);
        store.Compact(T0.AddMinutes(3));
        store.Compact(T0.AddMinutes(3));

        store.WriteBatch([Sample(T0.AddMinutes(5), 30)], []);
        store.Compact(T0.AddMinutes(10));

        var minutes = store.MinuteAggregates(T0, T0.AddHours(1));
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(5)], minutes.Select(m => m.Minute));
        Assert.All(minutes, m => Assert.Equal(1, m.SampleCount));
    }

    [Fact]
    public void DeletesExpiredDataButKeepsUnaggregatedRaw()
    {
        var store = OpenStore();
        var policy = new RetentionPolicy
        {
            RawSamples = TimeSpan.FromHours(1),
            MinuteAggregates = TimeSpan.FromHours(3),
            Events = TimeSpan.FromHours(2),
        };
        store.WriteBatch(
            [Sample(T0, 10), Sample(T0.AddHours(2), 20), Sample(T0.AddHours(4), 30)],
            [new ServerEvent(T0, "old"), new ServerEvent(T0.AddHours(3), "new")]);

        store.Compact(T0.AddHours(4).AddSeconds(30), policy);

        // 4시간째 샘플은 집계 지연 안쪽이라 원본으로 남는다.
        Assert.Equal([T0.AddHours(4)], store.RecentSamples(10).Select(s => s.Timestamp));
        // 1분 집계는 3시간 보존: 0시 집계는 삭제, 2시 집계는 유지
        Assert.Equal([T0.AddHours(2)], store.MinuteAggregates(T0, T0.AddDays(1)).Select(m => m.Minute));
        Assert.Equal(["new"], store.Events(T0, T0.AddDays(1)).Select(e => e.Type));
    }
}

public class MetricsWriterTests : StoreTestBase
{
    [Fact]
    public async Task FlushesPeriodicallyAsOneBatch()
    {
        var store = OpenStore();
        var time = new FakeTimeProvider(T0);
        await using var writer = new MetricsWriter(store, new MetricsWriterOptions { FlushInterval = TimeSpan.FromSeconds(5) }, time);

        writer.Add(Sample(T0, 10));
        writer.Add(Sample(T0.AddSeconds(1), 11));
        await writer.AddAsync(new ServerEvent(T0, "start"));
        Assert.Empty(store.RecentSamples(10));

        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntil(() => store.RecentSamples(10).Count == 2);
        Assert.Single(store.Events(T0, T0.AddMinutes(1)));
    }

    [Fact]
    public async Task DisposeWritesRemainingData()
    {
        var store = OpenStore();
        var writer = new MetricsWriter(store, time: new FakeTimeProvider(T0));
        writer.Add(Sample(T0, 10));
        await writer.AddAsync(new ServerEvent(T0, "stop"));
        await writer.DisposeAsync();

        Assert.Single(store.RecentSamples(10));
        Assert.Single(store.Events(T0, T0.AddMinutes(1)));
    }

    [Fact]
    public async Task DropsOldestSamplesWhenFull()
    {
        var store = OpenStore();
        var writer = new MetricsWriter(store, new MetricsWriterOptions { SampleCapacity = 3 }, new FakeTimeProvider(T0));
        for (var i = 0; i < 5; i++)
            writer.Add(Sample(T0.AddSeconds(i), i));
        await writer.DisposeAsync();

        Assert.Equal(2, writer.DroppedSamples);
        Assert.Equal([2.0, 3.0, 4.0], store.RecentSamples(10).Select(s => s.MsptAvg!.Value));
    }

    [Fact]
    public async Task WriteFailureIsLoggedAndDoesNotStopWriter()
    {
        var store = OpenStore();
        var logs = new List<string>();
        await using var writer = new MetricsWriter(store, time: new FakeTimeProvider(T0), log: logs.Add);

        // 이벤트 type은 NOT NULL이라 이 배치는 기록에 실패한다.
        await writer.AddAsync(new ServerEvent(T0, null!));
        writer.Flush();
        Assert.Single(logs);

        writer.Add(Sample(T0, 10));
        writer.Flush();
        Assert.Single(store.RecentSamples(10));
    }

    [Fact]
    public async Task CompactsOnInterval()
    {
        var store = OpenStore();
        var time = new FakeTimeProvider(T0);
        await using var writer = new MetricsWriter(store, new MetricsWriterOptions
        {
            FlushInterval = TimeSpan.FromMinutes(1),
            CompactInterval = TimeSpan.FromMinutes(3),
        }, time);

        writer.Add(Sample(T0, 10));
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20);
        }
        await WaitUntil(() => store.MinuteAggregates(T0, T0.AddHours(1)).Count == 1);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("제한 시간 안에 조건을 만족하지 못했습니다.");
    }
}
