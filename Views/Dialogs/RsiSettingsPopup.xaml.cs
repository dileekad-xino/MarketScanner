using CommunityToolkit.Maui.Views;
using MarketScanner.Models;
using System.Globalization;

namespace MarketScanner.Views.Dialogs;

public partial class RsiSettingsPopup : Popup
{
    private RsiSettings _working;

    public RsiSettingsPopup(RsiSettings current)
    {
        InitializeComponent();
        _working = current?.Clone() ?? RsiSettings.CreateDefaults();
        LoadFields(_working);
    }

    private void LoadFields(RsiSettings settings)
    {
        PeriodEntry.Text = settings.Period.ToString();
        OversoldEntry.Text = settings.Oversold.ToString("0.##");
        OverboughtEntry.Text = settings.Overbought.ToString("0.##");
        DaysEntry.Text = settings.HistoricalDays.ToString();
        ErrorLabel.IsVisible = false;
    }

    private void OnDefaultsClicked(object sender, EventArgs e)
    {
        _working = RsiSettings.CreateDefaults();
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

    private bool TryBuildSettings(out RsiSettings settings, out string error)
    {
        settings = _working.Clone();
        error = string.Empty;

        if (!int.TryParse(PeriodEntry.Text, out var period) || period < 2 || period > 200)
        {
            error = "Length must be between 2 and 200.";
            return false;
        }

        if (!double.TryParse(OversoldEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var oversold) || oversold <= 0 || oversold >= 50)
        {
            error = "Oversold must be between 0 and 50.";
            return false;
        }

        if (!double.TryParse(OverboughtEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var overbought) || overbought <= 50 || overbought >= 100)
        {
            error = "Overbought must be between 50 and 100.";
            return false;
        }

        if (!int.TryParse(DaysEntry.Text, out var days) || days < 1 || days > 60)
        {
            error = "Historical days must be between 1 and 60.";
            return false;
        }

        settings.Period = period;
        settings.Oversold = oversold;
        settings.Overbought = overbought;
        settings.HistoricalDays = days;
        return true;
    }
}

