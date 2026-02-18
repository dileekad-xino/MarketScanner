using MarketScanner.Models;
using Microsoft.Maui.Storage;

namespace MarketScanner.Services;

public sealed class CciSettingsService : ICciSettingsService
{
    private const string PreferencesKey = "cci_settings";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CciSettings _current = CciSettings.CreateDefaults();

    public CciSettings Current => _current;

    public event EventHandler<CciSettings>? SettingsChanged;

    public async Task<CciSettings> GetAsync(string? symbol = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var symbolKey = BuildPreferencesKey(symbol);
            var json = Preferences.Get(symbolKey, null);
            if (string.IsNullOrWhiteSpace(json) && !string.Equals(symbolKey, PreferencesKey, StringComparison.Ordinal))
            {
                json = Preferences.Get(PreferencesKey, null);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _current = CciSettings.CreateDefaults();
            }
            else
            {
                _current = System.Text.Json.JsonSerializer.Deserialize<CciSettings>(json)
                           ?? CciSettings.CreateDefaults();
            }
            _current.SellZoneMin = CciSettings.NormalizeSellZoneMin(_current.SellZoneMin, _current.SellZoneMax);
            _current.SellZoneMax = CciSettings.NormalizeSellZoneMax(_current.SellZoneMax, _current.SellZoneMin);
            _current.ZoneGapInterval = CciSettings.NormalizeZoneGapInterval(_current.ZoneGapInterval, _current.SellZoneMin, _current.SellZoneMax);
            return _current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CciSettings settings, string? symbol = null, CancellationToken ct = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _current = settings.Clone();
            _current.SellZoneMin = CciSettings.NormalizeSellZoneMin(_current.SellZoneMin, _current.SellZoneMax);
            _current.SellZoneMax = CciSettings.NormalizeSellZoneMax(_current.SellZoneMax, _current.SellZoneMin);
            _current.ZoneGapInterval = CciSettings.NormalizeZoneGapInterval(_current.ZoneGapInterval, _current.SellZoneMin, _current.SellZoneMax);
            var json = System.Text.Json.JsonSerializer.Serialize(_current);
            Preferences.Set(BuildPreferencesKey(symbol), json);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _current.Clone());
    }

    private static string BuildPreferencesKey(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return PreferencesKey;

        var normalized = symbol.Trim().ToUpperInvariant();
        return $"{PreferencesKey}:{normalized}";
    }
}

