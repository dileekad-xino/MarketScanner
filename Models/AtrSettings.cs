namespace MarketScanner.Models;

/// <summary>
/// Persisted ATR configuration used by ATR indicator and stop-stage logic.
/// </summary>
public class AtrSettings
{
    public const int DefaultAtrPeriod = 14;
    public const double DefaultImpulseAtrMultiplier = 0.5;
    public const double DefaultInitialStopAtrMultiplier = 1.4;
    public const double DefaultProfitLockArmAtrMultiplier = 1.0;
    public const double DefaultProfitLockStopAtrMultiplier = 0.4;
    public const double DefaultTrailingArmAtrMultiplier = 1.5;
    public const double DefaultTrailingAtrMultiplier = 1.7;

    public int AtrPeriod { get; set; } = DefaultAtrPeriod;
    public double ImpulseAtrMultiplier { get; set; } = DefaultImpulseAtrMultiplier;
    public double InitialStopAtrMultiplier { get; set; } = DefaultInitialStopAtrMultiplier;
    public double ProfitLockArmAtrMultiplier { get; set; } = DefaultProfitLockArmAtrMultiplier;
    public double ProfitLockStopAtrMultiplier { get; set; } = DefaultProfitLockStopAtrMultiplier;
    public double TrailingArmAtrMultiplier { get; set; } = DefaultTrailingArmAtrMultiplier;
    public double TrailingAtrMultiplier { get; set; } = DefaultTrailingAtrMultiplier;

    public static int NormalizeAtrPeriod(int value) => value is >= 2 and <= 200 ? value : DefaultAtrPeriod;

    public static double NormalizeImpulseAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultImpulseAtrMultiplier;

    public static double NormalizeInitialStopAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultInitialStopAtrMultiplier;

    public static double NormalizeProfitLockArmAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultProfitLockArmAtrMultiplier;

    public static double NormalizeProfitLockStopAtrMultiplier(double value) => value is >= 0 and <= 20 ? value : DefaultProfitLockStopAtrMultiplier;

    public static double NormalizeTrailingArmAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultTrailingArmAtrMultiplier;

    public static double NormalizeTrailingAtrMultiplier(double value) => value is > 0 and <= 20 ? value : DefaultTrailingAtrMultiplier;

    public static AtrSettings CreateDefaults() => new()
    {
        AtrPeriod = DefaultAtrPeriod,
        ImpulseAtrMultiplier = DefaultImpulseAtrMultiplier,
        InitialStopAtrMultiplier = DefaultInitialStopAtrMultiplier,
        ProfitLockArmAtrMultiplier = DefaultProfitLockArmAtrMultiplier,
        ProfitLockStopAtrMultiplier = DefaultProfitLockStopAtrMultiplier,
        TrailingArmAtrMultiplier = DefaultTrailingArmAtrMultiplier,
        TrailingAtrMultiplier = DefaultTrailingAtrMultiplier
    };

    public AtrSettings Clone() => new()
    {
        AtrPeriod = AtrPeriod,
        ImpulseAtrMultiplier = ImpulseAtrMultiplier,
        InitialStopAtrMultiplier = InitialStopAtrMultiplier,
        ProfitLockArmAtrMultiplier = ProfitLockArmAtrMultiplier,
        ProfitLockStopAtrMultiplier = ProfitLockStopAtrMultiplier,
        TrailingArmAtrMultiplier = TrailingArmAtrMultiplier,
        TrailingAtrMultiplier = TrailingAtrMultiplier
    };
}
