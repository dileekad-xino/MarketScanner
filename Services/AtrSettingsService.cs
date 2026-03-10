using MarketScanner.Models;
using Microsoft.Maui.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketScanner.Services;

public sealed class AtrSettingsService : IAtrSettingsService
{
    private const string PreferencesKey = "atr_settings";
    private const string LegacyCciPreferencesKey = "cci_settings";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AtrSettings _current = AtrSettings.CreateDefaults();

    public AtrSettings Current => _current;

    public event EventHandler<AtrSettings>? SettingsChanged;

    public async Task<AtrSettings> GetAsync(string? symbol = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = BuildPreferencesKey(symbol);
            var json = Preferences.Get(key, null);

            if (string.IsNullOrWhiteSpace(json) && !string.Equals(key, PreferencesKey, StringComparison.Ordinal))
            {
                json = Preferences.Get(PreferencesKey, null);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _current = LoadFromLegacyCciSettings(symbol) ?? AtrSettings.CreateDefaults();
            }
            else
            {
                _current = System.Text.Json.JsonSerializer.Deserialize<AtrSettings>(json) ?? AtrSettings.CreateDefaults();
                ApplyLegacyArmMappingIfNeeded(_current, json);
            }

            NormalizeCurrent();
            return _current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AtrSettings settings, string? symbol = null, CancellationToken ct = default)
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
        _current.AtrPeriod = AtrSettings.NormalizeAtrPeriod(_current.AtrPeriod);
        _current.ImpulseAtrMultiplier = AtrSettings.NormalizeImpulseAtrMultiplier(_current.ImpulseAtrMultiplier);
        _current.InitialStopAtrMultiplier = AtrSettings.NormalizeInitialStopAtrMultiplier(_current.InitialStopAtrMultiplier);
        _current.ProfitLockArmAtrMultiplier = AtrSettings.NormalizeProfitLockArmAtrMultiplier(_current.ProfitLockArmAtrMultiplier);
        _current.ProfitLockStopAtrMultiplier = AtrSettings.NormalizeProfitLockStopAtrMultiplier(_current.ProfitLockStopAtrMultiplier);
        _current.TrailingArmAtrMultiplier = AtrSettings.NormalizeTrailingArmAtrMultiplier(_current.TrailingArmAtrMultiplier);
        _current.TrailingAtrMultiplier = AtrSettings.NormalizeTrailingAtrMultiplier(_current.TrailingAtrMultiplier);
    }

    private static string BuildPreferencesKey(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return PreferencesKey;

        return $"{PreferencesKey}:{symbol.Trim().ToUpperInvariant()}";
    }

    private static string BuildLegacyCciKey(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return LegacyCciPreferencesKey;

        return $"{LegacyCciPreferencesKey}:{symbol.Trim().ToUpperInvariant()}";
    }

    private static AtrSettings? LoadFromLegacyCciSettings(string? symbol)
    {
        string? legacyJson = Preferences.Get(BuildLegacyCciKey(symbol), null);
        if (string.IsNullOrWhiteSpace(legacyJson) && !string.IsNullOrWhiteSpace(symbol))
        {
            legacyJson = Preferences.Get(LegacyCciPreferencesKey, null);
        }

        if (string.IsNullOrWhiteSpace(legacyJson))
            return null;

        var legacy = System.Text.Json.JsonSerializer.Deserialize<LegacyCciAtrSnapshot>(legacyJson);
        if (legacy == null)
            return null;

        return new AtrSettings
        {
            AtrPeriod = legacy.AtrPeriod ?? AtrSettings.DefaultAtrPeriod,
            ImpulseAtrMultiplier = legacy.ImpulseAtrMultiplier ?? AtrSettings.DefaultImpulseAtrMultiplier,
            InitialStopAtrMultiplier = AtrSettings.DefaultInitialStopAtrMultiplier,
            ProfitLockArmAtrMultiplier = AtrSettings.DefaultProfitLockArmAtrMultiplier,
            ProfitLockStopAtrMultiplier = AtrSettings.DefaultProfitLockStopAtrMultiplier,
            TrailingArmAtrMultiplier = legacy.TrailingArmAtrMultiplier ?? AtrSettings.DefaultTrailingArmAtrMultiplier,
            TrailingAtrMultiplier = legacy.TrailingAtrMultiplier ?? AtrSettings.DefaultTrailingAtrMultiplier
        };
    }

    private static void ApplyLegacyArmMappingIfNeeded(AtrSettings settings, string json)
    {
        if (settings.ProfitLockArmAtrMultiplier > 0)
            return;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("BreakEvenArmAtrMultiplier", out var legacyArmElement) &&
                legacyArmElement.ValueKind is JsonValueKind.Number &&
                legacyArmElement.TryGetDouble(out var legacyArmValue) &&
                legacyArmValue > 0)
            {
                settings.ProfitLockArmAtrMultiplier = legacyArmValue;
            }
        }
        catch
        {
            // Ignore malformed legacy payloads and fall back to defaults/normalization.
        }
    }

    private sealed class LegacyCciAtrSnapshot
    {
        [JsonPropertyName("AtrPeriod")]
        public int? AtrPeriod { get; init; }

        [JsonPropertyName("ImpulseAtrMultiplier")]
        public double? ImpulseAtrMultiplier { get; init; }

        [JsonPropertyName("TrailingArmAtrMultiplier")]
        public double? TrailingArmAtrMultiplier { get; init; }

        [JsonPropertyName("TrailingAtrMultiplier")]
        public double? TrailingAtrMultiplier { get; init; }
    }
}
