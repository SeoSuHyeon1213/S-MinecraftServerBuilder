using Microsoft.Data.Sqlite;

namespace Builder.Storage;

/// <summary>
/// 서버 폴더별 SQLite 지표 저장소 (<c>&lt;서버&gt;/builder-data/metrics.db</c>).
/// 서버 구성의 원본은 builder.json이며, 이 DB는 시계열 지표와 이벤트만 담는다.
/// </summary>
/// <remarks>
/// 호출마다 연결을 새로 연다. WAL 모드라 기록(<see cref="MetricsWriter"/>)과 조회가 서로 막지 않는다.
/// </remarks>
public sealed class MetricsStore
{
    public const string DataDirName = "builder-data";
    public const string FileName = "metrics.db";

    /// <summary>스키마를 바꾸면 올리고 <see cref="Migrate"/>에 단계를 추가한다.</summary>
    internal const int SchemaVersion = 1;

    private readonly string connectionString;

    private MetricsStore(string path)
    {
        DatabasePath = path;
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public string DatabasePath { get; }

    public static string PathFor(string serverDir) => Path.Combine(Path.GetFullPath(serverDir), DataDirName, FileName);

    public static MetricsStore OpenForServer(string serverDir) => Open(PathFor(serverDir));

    /// <summary>DB 파일이 없으면 만들고 스키마를 최신으로 맞춘다.</summary>
    public static MetricsStore Open(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var store = new MetricsStore(full);
        using var conn = store.Connect();
        Execute(conn, "PRAGMA journal_mode = WAL;");
        Migrate(conn);
        return store;
    }

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        // WAL에서는 NORMAL이어도 DB가 깨지지 않고, 전원 차단 시 마지막 몇 건만 잃는다. 지표 용도로 충분하다.
        Execute(conn, "PRAGMA synchronous = NORMAL;");
        return conn;
    }

    private static void Migrate(SqliteConnection conn)
    {
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"));
        if (version > SchemaVersion)
            throw new InvalidOperationException($"지표 DB 스키마({version})가 이 빌더가 아는 버전({SchemaVersion})보다 새롭습니다.");
        if (version == SchemaVersion)
            return;

        using var tx = conn.BeginTransaction();
        if (version < 1)
        {
            Execute(conn, """
                CREATE TABLE samples (
                    ts            INTEGER NOT NULL,
                    tps           REAL,
                    mspt_avg      REAL,
                    mspt_p95      REAL,
                    heap_used_mb  INTEGER,
                    rss_mb        INTEGER,
                    cpu_pct       REAL,
                    players       INTEGER
                );
                CREATE INDEX ix_samples_ts ON samples (ts);

                CREATE TABLE samples_1m (
                    ts            INTEGER PRIMARY KEY,
                    sample_count  INTEGER NOT NULL,
                    tps_avg       REAL,
                    tps_min       REAL,
                    mspt_avg      REAL,
                    mspt_max      REAL,
                    heap_used_max INTEGER,
                    rss_max       INTEGER,
                    cpu_avg       REAL,
                    players_max   INTEGER
                );

                CREATE TABLE events (
                    ts            INTEGER NOT NULL,
                    type          TEXT NOT NULL,
                    payload       TEXT
                );
                CREATE INDEX ix_events_ts ON events (ts);
                """, tx);
        }
        Execute(conn, $"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    /// <summary>샘플과 이벤트를 한 트랜잭션으로 기록한다. 건마다 커밋하지 않도록 묶어서 호출한다.</summary>
    public void WriteBatch(IReadOnlyCollection<MetricSample> samples, IReadOnlyCollection<ServerEvent> events)
    {
        if (samples.Count == 0 && events.Count == 0)
            return;

        using var conn = Connect();
        using var tx = conn.BeginTransaction();

        if (samples.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO samples (ts, tps, mspt_avg, mspt_p95, heap_used_mb, rss_mb, cpu_pct, players)
                VALUES ($ts, $tps, $mspt_avg, $mspt_p95, $heap, $rss, $cpu, $players);
                """;
            var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var tps = cmd.Parameters.Add("$tps", SqliteType.Real);
            var msptAvg = cmd.Parameters.Add("$mspt_avg", SqliteType.Real);
            var msptP95 = cmd.Parameters.Add("$mspt_p95", SqliteType.Real);
            var heap = cmd.Parameters.Add("$heap", SqliteType.Integer);
            var rss = cmd.Parameters.Add("$rss", SqliteType.Integer);
            var cpu = cmd.Parameters.Add("$cpu", SqliteType.Real);
            var players = cmd.Parameters.Add("$players", SqliteType.Integer);
            cmd.Prepare();

            foreach (var s in samples)
            {
                ts.Value = s.Timestamp.ToUnixTimeMilliseconds();
                tps.Value = Db(s.Tps);
                msptAvg.Value = Db(s.MsptAvg);
                msptP95.Value = Db(s.MsptP95);
                heap.Value = Db(s.HeapUsedMb);
                rss.Value = Db(s.RssMb);
                cpu.Value = Db(s.CpuPercent);
                players.Value = Db(s.Players);
                cmd.ExecuteNonQuery();
            }
        }

        if (events.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO events (ts, type, payload) VALUES ($ts, $type, $payload);";
            var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var type = cmd.Parameters.Add("$type", SqliteType.Text);
            var payload = cmd.Parameters.Add("$payload", SqliteType.Text);
            cmd.Prepare();

            foreach (var e in events)
            {
                ts.Value = e.Timestamp.ToUnixTimeMilliseconds();
                type.Value = e.Type;
                payload.Value = Db(e.Payload);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// 끝난 분의 원본 샘플을 1분 집계로 만들고, 보존 기간이 지난 데이터를 지운다.
    /// 이미 집계된 분 이후만 처리하므로 주기적으로 불러도 비용이 일정하다.
    /// </summary>
    public void Compact(DateTimeOffset now, RetentionPolicy? policy = null)
    {
        policy ??= RetentionPolicy.Default;
        var aggregateUntil = FloorToMinute(now - policy.AggregationDelay).ToUnixTimeMilliseconds();

        using var conn = Connect();
        using var tx = conn.BeginTransaction();

        Execute(conn, """
            INSERT OR REPLACE INTO samples_1m
                (ts, sample_count, tps_avg, tps_min, mspt_avg, mspt_max, heap_used_max, rss_max, cpu_avg, players_max)
            SELECT (ts / 60000) * 60000 AS minute, COUNT(*), AVG(tps), MIN(tps), AVG(mspt_avg),
                   MAX(COALESCE(mspt_p95, mspt_avg)), MAX(heap_used_mb), MAX(rss_mb), AVG(cpu_pct), MAX(players)
            FROM samples
            WHERE ts >= COALESCE((SELECT MAX(ts) + 60000 FROM samples_1m), 0) AND ts < $until
            GROUP BY minute;
            """, tx, ("$until", aggregateUntil));

        // 집계되지 않은 원본은 보존 기간이 지나도 남겨 두어, 오래 멈췄다 다시 켠 경우에도 집계가 빠지지 않게 한다.
        var rawCutoff = Math.Min((now - policy.RawSamples).ToUnixTimeMilliseconds(), aggregateUntil);
        Execute(conn, "DELETE FROM samples WHERE ts < $cutoff;", tx, ("$cutoff", rawCutoff));
        Execute(conn, "DELETE FROM samples_1m WHERE ts < $cutoff;", tx,
            ("$cutoff", (now - policy.MinuteAggregates).ToUnixTimeMilliseconds()));
        Execute(conn, "DELETE FROM events WHERE ts < $cutoff;", tx,
            ("$cutoff", (now - policy.Events).ToUnixTimeMilliseconds()));

        tx.Commit();
    }

    /// <summary>[from, to) 구간 원본 샘플 요약. MSPT 95백분위는 각 샘플의 평균 MSPT 분포에서 구한다.</summary>
    public MetricsSummary Summarize(DateTimeOffset from, DateTimeOffset to)
    {
        using var conn = Connect();
        (string, object)[] range = [("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())];

        using var cmd = Command(conn, """
            SELECT COUNT(*), AVG(tps), MIN(tps), AVG(mspt_avg), MAX(heap_used_mb), MAX(players)
            FROM samples WHERE ts >= $from AND ts < $to;
            """, null, range);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var count = reader.GetInt32(0);
        var tpsAvg = NullableDouble(reader, 1);
        var tpsMin = NullableDouble(reader, 2);
        var msptAvg = NullableDouble(reader, 3);
        var heapMax = NullableLong(reader, 4);
        var playersMax = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);

        var mspt = new List<double>();
        using (var msptCmd = Command(conn,
            "SELECT mspt_avg FROM samples WHERE ts >= $from AND ts < $to AND mspt_avg IS NOT NULL ORDER BY mspt_avg;",
            null, range))
        using (var msptReader = msptCmd.ExecuteReader())
        {
            while (msptReader.Read())
                mspt.Add(msptReader.GetDouble(0));
        }

        return new MetricsSummary(count, tpsAvg, tpsMin, msptAvg, Percentile(mspt, 0.95), heapMax, playersMax);
    }

    /// <summary>최근 원본 샘플을 오래된 순으로 돌려준다.</summary>
    public IReadOnlyList<MetricSample> RecentSamples(int limit)
    {
        using var conn = Connect();
        using var cmd = Command(conn, """
            SELECT ts, tps, mspt_avg, mspt_p95, heap_used_mb, rss_mb, cpu_pct, players
            FROM (SELECT * FROM samples ORDER BY ts DESC LIMIT $limit) ORDER BY ts;
            """, null, ("$limit", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<MetricSample>();
        while (r.Read())
        {
            list.Add(new MetricSample(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)))
            {
                Tps = NullableDouble(r, 1),
                MsptAvg = NullableDouble(r, 2),
                MsptP95 = NullableDouble(r, 3),
                HeapUsedMb = NullableLong(r, 4),
                RssMb = NullableLong(r, 5),
                CpuPercent = NullableDouble(r, 6),
                Players = r.IsDBNull(7) ? null : r.GetInt32(7),
            });
        }
        return list;
    }

    /// <summary>[from, to) 구간 1분 집계를 오래된 순으로 돌려준다.</summary>
    public IReadOnlyList<MinuteAggregate> MinuteAggregates(DateTimeOffset from, DateTimeOffset to)
    {
        using var conn = Connect();
        using var cmd = Command(conn, """
            SELECT ts, sample_count, tps_avg, tps_min, mspt_avg, mspt_max, heap_used_max, rss_max, cpu_avg, players_max
            FROM samples_1m WHERE ts >= $from AND ts < $to ORDER BY ts;
            """, null, ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
        using var r = cmd.ExecuteReader();
        var list = new List<MinuteAggregate>();
        while (r.Read())
        {
            list.Add(new MinuteAggregate(
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), r.GetInt32(1),
                NullableDouble(r, 2), NullableDouble(r, 3), NullableDouble(r, 4), NullableDouble(r, 5),
                NullableLong(r, 6), NullableLong(r, 7), NullableDouble(r, 8),
                r.IsDBNull(9) ? null : r.GetInt32(9)));
        }
        return list;
    }

    /// <summary>[from, to) 구간 이벤트를 오래된 순으로 돌려준다. <paramref name="type"/>을 주면 그 종류만.</summary>
    public IReadOnlyList<ServerEvent> Events(DateTimeOffset from, DateTimeOffset to, string? type = null)
    {
        using var conn = Connect();
        using var cmd = Command(conn, """
            SELECT ts, type, payload FROM events
            WHERE ts >= $from AND ts < $to AND ($type IS NULL OR type = $type) ORDER BY ts, rowid;
            """, null, ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()), ("$type", Db(type)));
        using var r = cmd.ExecuteReader();
        var list = new List<ServerEvent>();
        while (r.Read())
            list.Add(new ServerEvent(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    /// <summary>정렬된 값에서 최근접 순위(nearest-rank) 방식으로 백분위를 구한다.</summary>
    internal static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
            return null;
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds() / 60000 * 60000);

    private static object Db<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static double? NullableDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private static SqliteCommand Command(SqliteConnection conn, string sql, SqliteTransaction? tx,
        params (string Name, object Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private static void Execute(SqliteConnection conn, string sql, SqliteTransaction? tx = null,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(conn, sql, tx, parameters);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = Command(conn, sql, null);
        return cmd.ExecuteScalar();
    }
}
