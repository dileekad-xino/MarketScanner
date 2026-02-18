namespace MarketScanner.Models;

public sealed record AlgoRunnerSettingsResult(
    RsiSettings RsiSettings,
    CciSettings CciSettings
);
