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
        
        // Subscribe to mode picker changes to update unit label
        TrailingStopModePicker.SelectedIndexChanged += (s, e) => UpdateTrailingStopUnitLabel();
    }

    private void LoadFields(RsiSettings settings)
    {
        PeriodEntry.Text = settings.Period.ToString();
        OversoldEntry.Text = settings.Oversold.ToString("0.##");
        OverboughtEntry.Text = settings.Overbought.ToString("0.##");
        DaysEntry.Text = settings.HistoricalDays.ToString();
        
        // Set trailing stop mode picker
        TrailingStopModePicker.SelectedIndex = settings.TrailingStopMode == TrailingStopMode.Percentage ? 0 : 1;
        UpdateTrailingStopUnitLabel();
        
        // Set trailing stop distance
        TrailingStopDistanceEntry.Text = settings.TrailingStopDistance.ToString("0.##");
        
        // Set initial stop-loss
        InitialStopLossEntry.Text = settings.InitialStopLossPercent.ToString("0.##");
        
        // Set activation price
        ActivationPriceEntry.Text = settings.TrailingStopActivationPercent.ToString("0.##");
        
        // Set bar size picker
        var validBarSizes = new[] { "15 secs", "30 secs", "1 min" };
        var barSizeIndex = Array.IndexOf(validBarSizes, settings.BarSize);
        if (barSizeIndex >= 0)
        {
            BarSizePicker.SelectedIndex = barSizeIndex;
        }
        else
        {
            // Default to "1 min" if not found
            BarSizePicker.SelectedIndex = 2;
        }
        
        ErrorLabel.IsVisible = false;
    }
    
    private void UpdateTrailingStopUnitLabel()
    {
        if (TrailingStopModePicker.SelectedIndex == 0) // Percentage
        {
            TrailingStopUnitLabel.Text = "%";
        }
        else // Price
        {
            TrailingStopUnitLabel.Text = "$";
        }
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

        if (BarSizePicker.SelectedIndex < 0)
        {
            error = "Please select a bar size interval.";
            return false;
        }

        // Validate initial stop-loss
        if (!double.TryParse(InitialStopLossEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialStopLoss) || initialStopLoss <= 0 || initialStopLoss > 10)
        {
            error = "Initial stop-loss must be between 0.1 and 10 percent.";
            return false;
        }

        if (TrailingStopModePicker.SelectedIndex < 0)
        {
            error = "Please select a trailing stop mode.";
            return false;
        }

        // Validate trailing stop distance based on mode
        if (!double.TryParse(TrailingStopDistanceEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingStopDistance))
        {
            error = "Trailing stop distance must be a valid number.";
            return false;
        }

        var trailingStopMode = TrailingStopModePicker.SelectedIndex == 0 ? TrailingStopMode.Percentage : TrailingStopMode.Price;
        
        if (trailingStopMode == TrailingStopMode.Percentage)
        {
            // Percentage mode: validate 0.1% to 10%
            if (trailingStopDistance <= 0 || trailingStopDistance > 10)
            {
                error = "Trailing stop percentage must be between 0.1 and 10.";
                return false;
            }
        }
        else // Price mode
        {
            // Price mode: validate $0.01 to $100.00
            if (trailingStopDistance <= 0 || trailingStopDistance > 100)
            {
                error = "Trailing stop price distance must be between $0.01 and $100.00.";
                return false;
            }
        }
        
        // Validate activation price
        if (!double.TryParse(ActivationPriceEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var activationPrice) || activationPrice <= 0 || activationPrice > 20)
        {
            error = "Activation price must be between 0.1 and 20 percent.";
            return false;
        }

        var validBarSizes = new[] { "15 secs", "30 secs", "1 min" };
        var barSize = validBarSizes[BarSizePicker.SelectedIndex];

        settings.Period = period;
        settings.Oversold = oversold;
        settings.Overbought = overbought;
        settings.HistoricalDays = days;
        settings.BarSize = barSize;
        settings.InitialStopLossPercent = initialStopLoss;
        settings.TrailingStopMode = trailingStopMode;
        settings.TrailingStopDistance = trailingStopDistance;
        settings.TrailingStopActivationPercent = activationPrice;
        // Keep TrailingStopPoints for backward compatibility (deprecated)
        settings.TrailingStopPoints = trailingStopDistance;
        return true;
    }
}

