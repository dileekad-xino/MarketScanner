namespace MarketScanner.Utilities;

/// <summary>
/// Single source of truth for timeframe conversions used across the app.
/// Update this file when adding/removing supported timeframes.
/// </summary>
public static class TimeframeMap
{
    private static readonly IReadOnlyDictionary<int, (string IntervalKey, string IbBarSize)> _map =
        new Dictionary<int, (string IntervalKey, string IbBarSize)>
        {
            [5] = ("5s", "5 secs"),
            [10] = ("10s", "10 secs"),
            [15] = ("15s", "15 secs"),
            [30] = ("30s", "30 secs"),
            [60] = ("1min", "1 min"),
            [120] = ("2min", "2 mins"),
            [300] = ("5min", "5 mins")
        };

    public static string ToIntervalKey(int seconds)
    {
        return _map.TryGetValue(seconds, out var value)
            ? value.IntervalKey
            : $"{seconds}s";
    }

    public static string ToIbBarSize(int seconds)
    {
        return _map.TryGetValue(seconds, out var value)
            ? value.IbBarSize
            : $"{Math.Max(1, seconds)} secs";
    }
}
