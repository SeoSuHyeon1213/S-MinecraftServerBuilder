using System.Globalization;
using System.Text.RegularExpressions;

namespace Builder.Storage.Collection;

/// <summary>틱 상태. 값은 응답에 있는 것만 채운다.</summary>
public sealed record TickStatus(double? Tps, double? MsptAvg, double? MsptP95);

public sealed record PlayerList(int Online, int Max, IReadOnlyList<string> Names);

/// <summary>
/// RCON 명령 응답 파서. 서버 메시지는 서버 언어(en_us)로 오며, RCON은 여러 줄 메시지를 줄바꿈 없이 이어 붙이기도 하므로
/// 줄 구분에 기대지 않는다. 소수점은 서버 OS 로캘에 따라 쉼표일 수 있다.
/// </summary>
public static partial class StatusParsers
{
    /// <summary>바닐라 <c>/tick query</c> (Minecraft 1.20.3+).</summary>
    public static TickStatus? ParseTickQuery(string response)
    {
        var avg = Number(TickAverage().Match(response));
        if (avg is null)
            return null;

        var target = Number(TickTarget().Match(response)) ?? 20;
        var p95 = Number(TickP95().Match(response));
        double tps;
        if (response.Contains("is frozen", StringComparison.OrdinalIgnoreCase))
            tps = 0;
        else
            tps = avg > 0 ? Math.Min(target, 1000 / avg.Value) : target;
        return new TickStatus(Math.Round(tps, 2), avg, p95);
    }

    /// <summary>
    /// spark <c>/spark tps</c>. TPS는 첫 값(최근 5초), MSPT는 첫 구간(최근 10초)의 중앙값과 95백분위를 쓴다.
    /// spark는 평균이 아니라 중앙값을 주므로 <see cref="TickStatus.MsptAvg"/>에는 중앙값이 들어간다.
    /// </summary>
    public static TickStatus? ParseSparkTps(string response)
    {
        var tpsMatch = SparkTps().Match(response);
        var durationMatch = SparkDurations().Match(response);
        if (!tpsMatch.Success && !durationMatch.Success)
            return null;

        double? tps = tpsMatch.Success ? Parse(tpsMatch.Groups[1].Value) : null;
        double? median = durationMatch.Success ? Parse(durationMatch.Groups[2].Value) : null;
        double? p95 = durationMatch.Success ? Parse(durationMatch.Groups[3].Value) : null;
        return new TickStatus(tps, median, p95);
    }

    /// <summary>바닐라 <c>/list</c>.</summary>
    public static PlayerList? ParsePlayerList(string response)
    {
        var m = PlayerListPattern().Match(response);
        if (!m.Success)
            return null;
        var names = m.Groups[3].Value
            .Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return new PlayerList(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), names);
    }

    /// <summary>명령이 없는 서버(구버전, spark 미설치)의 응답인지.</summary>
    public static bool IsUnknownCommand(string response) =>
        response.Contains("Unknown or incomplete command", StringComparison.OrdinalIgnoreCase) ||
        response.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);

    private static double? Number(Match m) => m.Success ? Parse(m.Groups[1].Value) : null;

    private static double? Parse(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"Average time per tick:\s*([0-9]+(?:[.,][0-9]+)?)\s*ms", RegexOptions.IgnoreCase)]
    private static partial Regex TickAverage();

    [GeneratedRegex(@"Target tick rate:\s*([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TickTarget();

    [GeneratedRegex(@"P95:\s*([0-9]+(?:[.,][0-9]+)?)\s*ms", RegexOptions.IgnoreCase)]
    private static partial Regex TickP95();

    // "TPS from last 5s, 10s, 1m, 5m, 15m:" 다음 줄의 첫 값. 한계(20)에 닿으면 앞에 '*'가 붙는다.
    [GeneratedRegex(@"TPS from last[^:]*:\s*\*?([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SparkTps();

    // "Tick durations (min/med/95%ile/max ms) from last 10s, 1m:" 다음의 "min/med/95/max"
    [GeneratedRegex(@"Tick durations[^:]*:\s*([0-9.]+)/([0-9.]+)/([0-9.]+)/([0-9.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SparkDurations();

    [GeneratedRegex(@"There are (\d+) of a max(?: of)? (\d+) players online:?(.*)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PlayerListPattern();
}
