namespace MarketScanner.Models;

/// <summary>
/// Persisted CCI/ATR momentum configuration used by indicator algorithms and settings dialogs.
/// </summary>
public class CciSettings
{
    public const int DefaultPeriod = 14;
    public const int DefaultAtrPeriod = 14;
    public const double DefaultEntryThreshold = 100.0;
    public const double DefaultEntryMinDelta = 8.0;
    public const double DefaultImpulseAtrMultiplier = 0.5;
    public const double DefaultTrailingAtrMultiplier = 1.7;
    public const double DefaultTrailingArmAtrMultiplier = 2.5;
    public const bool DefaultRequireRisingEma20 = true;

    public int Period { get; set; } = DefaultPeriod;
    public int AtrPeriod { get; set; } = DefaultAtrPeriod;
    public int HistoricalDays { get; set; } = 2;

    public double EntryThreshold { get; set; } = DefaultEntryThreshold;
    public double EntryMinDelta { get; set; } = DefaultEntryMinDelta;
    public double ImpulseAtrMultiplier { get; set; } = DefaultImpulseAtrMultiplier;
    public double TrailingAtrMultiplier { get; set; } = DefaultTrailingAtrMultiplier;
    public double TrailingArmAtrMultiplier { get; set; } = DefaultTrailingArmAtrMultiplier;
    public bool RequireRisingEma20 { get; set; } = DefaultRequireRisingEma20;

    // Backward compatibility for existing persisted JSON that still uses Overbought.
    public double Overbought
    {
        get => EntryThreshold;
        set => EntryThreshold = value;
    }

    public static int NormalizePeriod(int period) => period is >= 2 and <= 200 ? period : DefaultPeriod;

    public static int NormalizeAtrPeriod(int atrPeriod) => atrPeriod is >= 2 and <= 200 ? atrPeriod : DefaultAtrPeriod;

    public static double NormalizeEntryThreshold(double value) => value is > 0 and <= 400 ? value : DefaultEntryThreshold;

    public static double NormalizeEntryMinDelta(double value) => value is >= 0 and <= 200 ? value : DefaultEntryMinDelta;

    public static double NormalizeImpulseAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultImpulseAtrMultiplier;

    public static double NormalizeTrailingAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultTrailingAtrMultiplier;

    public static double NormalizeTrailingArmAtrMultiplier(double value) => value is > 0 and <= 50 ? value : DefaultTrailingArmAtrMultiplier;

    public static CciSettings CreateDefaults() => new()
    {
        Period = DefaultPeriod,
        AtrPeriod = DefaultAtrPeriod,
        HistoricalDays = 2,
        EntryThreshold = DefaultEntryThreshold,
        EntryMinDelta = DefaultEntryMinDelta,
        ImpulseAtrMultiplier = DefaultImpulseAtrMultiplier,
        TrailingAtrMultiplier = DefaultTrailingAtrMultiplier,
        TrailingArmAtrMultiplier = DefaultTrailingArmAtrMultiplier,
        RequireRisingEma20 = DefaultRequireRisingEma20
    };

    public CciSettings Clone() => new()
    {
        Period = Period,
        AtrPeriod = AtrPeriod,
        HistoricalDays = HistoricalDays,
        EntryThreshold = EntryThreshold,
        EntryMinDelta = EntryMinDelta,
        ImpulseAtrMultiplier = ImpulseAtrMultiplier,
        TrailingAtrMultiplier = TrailingAtrMultiplier,
        TrailingArmAtrMultiplier = TrailingArmAtrMultiplier,
        RequireRisingEma20 = RequireRisingEma20
    };
}
