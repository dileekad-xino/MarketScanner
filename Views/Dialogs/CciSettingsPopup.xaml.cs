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
        PeriodEntry.Text = settings.Period.ToString(CultureInfo.InvariantCulture);
        OversoldEntry.Text = settings.Oversold.ToString("0.##", CultureInfo.InvariantCulture);
        OverboughtEntry.Text = settings.Overbought.ToString("0.##", CultureInfo.InvariantCulture);
        DaysEntry.Text = settings.HistoricalDays.ToString(CultureInfo.InvariantCulture);
        var sellZoneMin = CciSettings.NormalizeSellZoneMin(settings.SellZoneMin, settings.SellZoneMax);
        var sellZoneMax = CciSettings.NormalizeSellZoneMax(settings.SellZoneMax, sellZoneMin);
        SellZoneMinEntry.Text = sellZoneMin.ToString(CultureInfo.InvariantCulture);
        SellZoneMaxEntry.Text = sellZoneMax.ToString(CultureInfo.InvariantCulture);
        ZoneGapIntervalEntry.Text = CciSettings.NormalizeZoneGapInterval(settings.ZoneGapInterval, sellZoneMin, sellZoneMax).ToString(CultureInfo.InvariantCulture);
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
            error = "Length must be between 2 and 200.";
            return false;
        }

        if (!double.TryParse(OversoldEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var oversold) || oversold < -400 || oversold >= 0)
        {
            error = "Oversold must be between -400 and 0.";
            return false;
        }

        if (!double.TryParse(OverboughtEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var overbought) || overbought <= 0 || overbought > 400)
        {
            error = "Overbought must be between 0 and 400.";
            return false;
        }

        if (overbought <= oversold)
        {
            error = "Overbought must be greater than oversold.";
            return false;
        }

        if (!int.TryParse(DaysEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 60)
        {
            error = "Historical days must be between 1 and 60.";
            return false;
        }

        if (!int.TryParse(SellZoneMinEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sellZoneMin) ||
            !CciSettings.IsValidSellZoneMin(sellZoneMin))
        {
            error = "Sell zone min must be greater than 0.";
            return false;
        }

        if (!int.TryParse(SellZoneMaxEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sellZoneMax) ||
            !CciSettings.IsValidSellZoneMax(sellZoneMax))
        {
            error = "Sell zone max must be greater than 0.";
            return false;
        }

        if (!CciSettings.IsValidSellZoneBounds(sellZoneMin, sellZoneMax))
        {
            error = "Sell zone max must be greater than sell zone min.";
            return false;
        }

        if (!int.TryParse(ZoneGapIntervalEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zoneGapInterval) ||
            !CciSettings.IsValidZoneGapInterval(zoneGapInterval, sellZoneMin, sellZoneMax))
        {
            var range = CciSettings.GetSellZoneRange(sellZoneMin, sellZoneMax);
            error = $"Zone gap must evenly divide {range} (Sell Zone Max - Sell Zone Min).";
            return false;
        }

        settings.Period = period;
        settings.Oversold = oversold;
        settings.Overbought = overbought;
        settings.HistoricalDays = days;
        settings.SellZoneMin = sellZoneMin;
        settings.SellZoneMax = sellZoneMax;
        settings.ZoneGapInterval = zoneGapInterval;
        return true;
    }
}
