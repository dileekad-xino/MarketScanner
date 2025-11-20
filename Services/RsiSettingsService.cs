using MarketScanner.Models;
using Microsoft.Maui.Storage;

namespace MarketScanner.Services;

public sealed class RsiSettingsService : IRsiSettingsService
{
    private const string PreferencesKey = "rsi_settings";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RsiSettings _current = RsiSettings.CreateDefaults();

    public RsiSettings Current => _current;

    public event EventHandler<RsiSettings>? SettingsChanged;

    public async Task<RsiSettings> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = Preferences.Get(PreferencesKey, null);
            if (string.IsNullOrWhiteSpace(json))
            {
                _current = RsiSettings.CreateDefaults();
            }
            else
            {
                _current = System.Text.Json.JsonSerializer.Deserialize<RsiSettings>(json)
                           ?? RsiSettings.CreateDefaults();
            }
            return _current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RsiSettings settings, CancellationToken ct = default)
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

