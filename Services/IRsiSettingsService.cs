using MarketScanner.Models;

namespace MarketScanner.Services;

public interface IRsiSettingsService
{
    RsiSettings Current { get; }
    Task<RsiSettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(RsiSettings settings, CancellationToken ct = default);
    event EventHandler<RsiSettings>? SettingsChanged;
}

