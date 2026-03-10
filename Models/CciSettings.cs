namespace MarketScanner.Models;

/// <summary>
/// Persisted CCI momentum configuration used by CCI indicator and entry-momentum logic.
/// </summary>
public class CciSettings
{
    public const int DefaultPeriod = 14;
    public const double DefaultEntryThreshold = 100.0;
    public const double DefaultEntryMinDelta = 8.0;
    public const bool DefaultRequireRisingEma20 = true;

    public int Period { get; set; } = DefaultPeriod;
    public int HistoricalDays { get; set; } = 2;

    public double EntryThreshold { get; set; } = DefaultEntryThreshold;
    public double EntryMinDelta { get; set; } = DefaultEntryMinDelta;
    public bool RequireRisingEma20 { get; set; } = DefaultRequireRisingEma20;

    // Backward compatibility for existing persisted JSON that still uses Overbought.
    public double Overbought
    {
        get => EntryThreshold;
        set => EntryThreshold = value;
    }

    public static int NormalizePeriod(int period) => period is >= 2 and <= 200 ? period : DefaultPeriod;

    public static double NormalizeEntryThreshold(double value) => value is > 0 and <= 400 ? value : DefaultEntryThreshold;

    public static double NormalizeEntryMinDelta(double value) => value is >= 0 and <= 200 ? value : DefaultEntryMinDelta;

    public static CciSettings CreateDefaults() => new()
    {
        Period = DefaultPeriod,
        HistoricalDays = 2,
        EntryThreshold = DefaultEntryThreshold,
        EntryMinDelta = DefaultEntryMinDelta,
        RequireRisingEma20 = DefaultRequireRisingEma20
    };

    public CciSettings Clone() => new()
    {
        Period = Period,
        HistoricalDays = HistoricalDays,
        EntryThreshold = EntryThreshold,
        EntryMinDelta = EntryMinDelta,
        RequireRisingEma20 = RequireRisingEma20
    };
}
