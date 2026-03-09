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

            NormalizeCurrent();
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
            NormalizeCurrent();
            var json = System.Text.Json.JsonSerializer.Serialize(_current);
            Preferences.Set(BuildPreferencesKey(symbol), json);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _current.Clone());
    }

    private void NormalizeCurrent()
    {
        _current.Period = CciSettings.NormalizePeriod(_current.Period);
        _current.AtrPeriod = CciSettings.NormalizeAtrPeriod(_current.AtrPeriod);
        _current.HistoricalDays = _current.HistoricalDays is >= 1 and <= 60 ? _current.HistoricalDays : 2;
        _current.EntryThreshold = CciSettings.NormalizeEntryThreshold(_current.EntryThreshold);
        _current.EntryMinDelta = CciSettings.NormalizeEntryMinDelta(_current.EntryMinDelta);
        _current.ImpulseAtrMultiplier = CciSettings.NormalizeImpulseAtrMultiplier(_current.ImpulseAtrMultiplier);
        _current.TrailingAtrMultiplier = CciSettings.NormalizeTrailingAtrMultiplier(_current.TrailingAtrMultiplier);
        _current.TrailingArmAtrMultiplier = CciSettings.NormalizeTrailingArmAtrMultiplier(_current.TrailingArmAtrMultiplier);
    }

    private static string BuildPreferencesKey(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return PreferencesKey;

        var normalized = symbol.Trim().ToUpperInvariant();
        return $"{PreferencesKey}:{normalized}";
    }
}
