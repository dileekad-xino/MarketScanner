using MarketScanner.Models;

namespace MarketScanner.Services;

public interface IAtrSettingsService
{
    AtrSettings Current { get; }
    Task<AtrSettings> GetAsync(string? symbol = null, CancellationToken ct = default);
    Task SaveAsync(AtrSettings settings, string? symbol = null, CancellationToken ct = default);
    event EventHandler<AtrSettings>? SettingsChanged;
}
