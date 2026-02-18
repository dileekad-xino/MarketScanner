namespace MarketScanner.Models;

/// <summary>
/// Persisted CCI configuration used by the CCI algorithm and settings dialog.
/// </summary>
public class CciSettings
{
    public const int DefaultSellZoneMin = 100;
    public const int DefaultSellZoneMax = 400;
    public const int DefaultZoneGapInterval = 20;

    public int Period { get; set; } = 14;  // Default for 1m timeframe
    public double Overbought { get; set; } = 100.0;  // Standard overbought level
    public double Oversold { get; set; } = -100.0;  // Standard oversold level
    public int HistoricalDays { get; set; } = 2;
    public int SellZoneMin { get; set; } = DefaultSellZoneMin;
    public int SellZoneMax { get; set; } = DefaultSellZoneMax;
    public int ZoneGapInterval { get; set; } = DefaultZoneGapInterval;

    public static int GetSellZoneRange(int sellZoneMin, int sellZoneMax) =>
        sellZoneMax - sellZoneMin;

    public static bool IsValidSellZoneMin(int sellZoneMin) =>
        sellZoneMin > 0;

    public static bool IsValidSellZoneMax(int sellZoneMax) =>
        sellZoneMax > 0;

    public static bool IsValidSellZoneBounds(int sellZoneMin, int sellZoneMax) =>
        IsValidSellZoneMin(sellZoneMin) &&
        IsValidSellZoneMax(sellZoneMax) &&
        sellZoneMax > sellZoneMin;

    public static int NormalizeSellZoneMin(int sellZoneMin, int sellZoneMax)
    {
        var min = IsValidSellZoneMin(sellZoneMin) ? sellZoneMin : DefaultSellZoneMin;
        var max = IsValidSellZoneMax(sellZoneMax) ? sellZoneMax : DefaultSellZoneMax;
        if (max <= min)
            return Math.Max(1, max - 1);
        return min;
    }

    public static int NormalizeSellZoneMax(int sellZoneMax, int sellZoneMin)
    {
        var min = IsValidSellZoneMin(sellZoneMin) ? sellZoneMin : DefaultSellZoneMin;
        var max = IsValidSellZoneMax(sellZoneMax) ? sellZoneMax : DefaultSellZoneMax;
        if (max <= min)
            return min + 1;
        return max;
    }

    public static bool IsValidZoneGapInterval(int gap, int sellZoneMin, int sellZoneMax)
    {
        var min = NormalizeSellZoneMin(sellZoneMin, sellZoneMax);
        var max = NormalizeSellZoneMax(sellZoneMax, min);
        var range = GetSellZoneRange(min, max);
        return gap > 0 && range > 0 && range % gap == 0;
    }

    public static int NormalizeZoneGapInterval(int gap, int sellZoneMin, int sellZoneMax)
    {
        var min = NormalizeSellZoneMin(sellZoneMin, sellZoneMax);
        var max = NormalizeSellZoneMax(sellZoneMax, min);
        if (IsValidZoneGapInterval(gap, min, max))
            return gap;

        var range = GetSellZoneRange(min, max);
        if (range <= 0)
            return 1;

        if (range % DefaultZoneGapInterval == 0)
            return DefaultZoneGapInterval;

        for (var candidate = Math.Min(DefaultZoneGapInterval, range); candidate >= 1; candidate--)
        {
            if (range % candidate == 0)
                return candidate;
        }

        return 1;
    }

    public static double CalculateTrailingSellThreshold(double cciValue, int sellZoneMin, int sellZoneMax, int zoneGapInterval)
    {
        var min = NormalizeSellZoneMin(sellZoneMin, sellZoneMax);
        var max = NormalizeSellZoneMax(sellZoneMax, min);
        var gap = NormalizeZoneGapInterval(zoneGapInterval, min, max);
        if (cciValue <= min)
            return min;

        var clampedCci = Math.Min(cciValue, max);
        var steps = Math.Floor((clampedCci - min) / gap);
        return min + (steps * gap);
    }

    public static CciSettings CreateDefaults() => new()
    {
        Period = 14,
        Overbought = 100.0,
        Oversold = -100.0,
        HistoricalDays = 2,
        SellZoneMin = DefaultSellZoneMin,
        SellZoneMax = DefaultSellZoneMax,
        ZoneGapInterval = DefaultZoneGapInterval
    };

    public CciSettings Clone() => new()
    {
        Period = Period,
        Overbought = Overbought,
        Oversold = Oversold,
        HistoricalDays = HistoricalDays,
        SellZoneMin = SellZoneMin,
        SellZoneMax = SellZoneMax,
        ZoneGapInterval = ZoneGapInterval
    };
}

