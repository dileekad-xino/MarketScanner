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

    public async Task<CciSettings> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = Preferences.Get(PreferencesKey, null);
            if (string.IsNullOrWhiteSpace(json))
            {
                _current = CciSettings.CreateDefaults();
            }
            else
            {
                _current = System.Text.Json.JsonSerializer.Deserialize<CciSettings>(json)
                           ?? CciSettings.CreateDefaults();
            }
            return _current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CciSettings settings, CancellationToken ct = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _current = settings.Clone();
            var json = System.Text.Json.JsonSerializer.Serialize(_current);
            Preferences.Set(PreferencesKey, json);
        }
        finally
        {
            _gate.Release();
        }

        SettingsChanged?.Invoke(this, _current.Clone());
    }
}

