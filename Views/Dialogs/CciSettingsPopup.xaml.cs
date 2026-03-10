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
        EntryThresholdEntry.Text = CciSettings.NormalizeEntryThreshold(settings.EntryThreshold).ToString("0.##", CultureInfo.InvariantCulture);
        EntryMinDeltaEntry.Text = CciSettings.NormalizeEntryMinDelta(settings.EntryMinDelta).ToString("0.##", CultureInfo.InvariantCulture);
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

        if (!int.TryParse(DaysEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 60)
        {
            error = "Historical days must be between 1 and 60.";
            return false;
        }

        settings.Period = period;
        settings.EntryThreshold = entryThreshold;
        settings.EntryMinDelta = entryMinDelta;
        settings.RequireRisingEma20 = RequireRisingEma20Switch.IsToggled;
        settings.HistoricalDays = days;
        return true;
    }
}
