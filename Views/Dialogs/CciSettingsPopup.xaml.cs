using CommunityToolkit.Maui.Views;
using MarketScanner.Models;
using System.Globalization;

namespace MarketScanner.Views.Dialogs;

public partial class CciSettingsPopup : Popup
{
    private CciSettings _working;

    public CciSettingsPopup(CciSettings current)
    {
        InitializeComponent();
        _working = current?.Clone() ?? CciSettings.CreateDefaults();
        LoadFields(_working);
    }

    private void LoadFields(CciSettings settings)
    {
        PeriodEntry.Text = CciSettings.NormalizePeriod(settings.Period).ToString(CultureInfo.InvariantCulture);
        AtrPeriodEntry.Text = CciSettings.NormalizeAtrPeriod(settings.AtrPeriod).ToString(CultureInfo.InvariantCulture);
        EntryThresholdEntry.Text = CciSettings.NormalizeEntryThreshold(settings.EntryThreshold).ToString("0.##", CultureInfo.InvariantCulture);
        EntryMinDeltaEntry.Text = CciSettings.NormalizeEntryMinDelta(settings.EntryMinDelta).ToString("0.##", CultureInfo.InvariantCulture);
        ImpulseAtrMultiplierEntry.Text = CciSettings.NormalizeImpulseAtrMultiplier(settings.ImpulseAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        TrailingAtrMultiplierEntry.Text = CciSettings.NormalizeTrailingAtrMultiplier(settings.TrailingAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        TrailingArmAtrMultiplierEntry.Text = CciSettings.NormalizeTrailingArmAtrMultiplier(settings.TrailingArmAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        RequireRisingEma20Switch.IsToggled = settings.RequireRisingEma20;
        DaysEntry.Text = (settings.HistoricalDays is >= 1 and <= 60 ? settings.HistoricalDays : 2).ToString(CultureInfo.InvariantCulture);
        ErrorLabel.IsVisible = false;
    }

    private void OnDefaultsClicked(object sender, EventArgs e)
    {
        _working = CciSettings.CreateDefaults();
        LoadFields(_working);
    }

    private void OnCancelClicked(object sender, EventArgs e)
    {
        Close(null);
    }

    private void OnOkClicked(object sender, EventArgs e)
    {
        if (!TryBuildSettings(out var updated, out var error))
        {
            ErrorLabel.Text = error;
            ErrorLabel.IsVisible = true;
            return;
        }

        Close(updated);
    }

    private bool TryBuildSettings(out CciSettings settings, out string error)
    {
        settings = _working.Clone();
        error = string.Empty;

        if (!int.TryParse(PeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) || period < 2 || period > 200)
        {
            error = "CCI period must be between 2 and 200.";
            return false;
        }

        if (!int.TryParse(AtrPeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var atrPeriod) || atrPeriod < 2 || atrPeriod > 200)
        {
            error = "ATR period must be between 2 and 200.";
            return false;
        }

        if (!double.TryParse(EntryThresholdEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var entryThreshold) || entryThreshold <= 0 || entryThreshold > 400)
        {
            error = "Entry threshold must be between 0 and 400.";
            return false;
        }

        if (!double.TryParse(EntryMinDeltaEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var entryMinDelta) || entryMinDelta < 0 || entryMinDelta > 200)
        {
            error = "Entry min delta must be between 0 and 200.";
            return false;
        }

        if (!double.TryParse(ImpulseAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var impulseAtrMultiplier) || impulseAtrMultiplier <= 0 || impulseAtrMultiplier > 20)
        {
            error = "Impulse ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(TrailingAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingAtrMultiplier) || trailingAtrMultiplier <= 0 || trailingAtrMultiplier > 20)
        {
            error = "Trailing ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(TrailingArmAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingArmAtrMultiplier) || trailingArmAtrMultiplier <= 0 || trailingArmAtrMultiplier > 50)
        {
            error = "Trail arm ATR multiplier must be between 0 and 50.";
            return false;
        }

        if (!int.TryParse(DaysEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 60)
        {
            error = "Historical days must be between 1 and 60.";
            return false;
        }

        settings.Period = period;
        settings.AtrPeriod = atrPeriod;
        settings.EntryThreshold = entryThreshold;
        settings.EntryMinDelta = entryMinDelta;
        settings.ImpulseAtrMultiplier = impulseAtrMultiplier;
        settings.TrailingAtrMultiplier = trailingAtrMultiplier;
        settings.TrailingArmAtrMultiplier = trailingArmAtrMultiplier;
        settings.RequireRisingEma20 = RequireRisingEma20Switch.IsToggled;
        settings.HistoricalDays = days;
        return true;
    }
}
