using MarketScanner.Models;

namespace MarketScanner.Services;

public interface ICciSettingsService
{
    CciSettings Current { get; }
    Task<CciSettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(CciSettings settings, CancellationToken ct = default);
    event EventHandler<CciSettings>? SettingsChanged;
}

