using MarketScanner.Models;

namespace MarketScanner.Services;

public interface ICciSettingsService
{
    CciSettings Current { get; }
    Task<CciSettings> GetAsync(string? symbol = null, CancellationToken ct = default);
    Task SaveAsync(CciSettings settings, string? symbol = null, CancellationToken ct = default);
    event EventHandler<CciSettings>? SettingsChanged;
}

