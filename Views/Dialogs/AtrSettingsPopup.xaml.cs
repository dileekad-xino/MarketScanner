using CommunityToolkit.Maui.Views;
using MarketScanner.Models;
using System.Globalization;

namespace MarketScanner.Views.Dialogs;

public partial class AtrSettingsPopup : Popup
{
    private AtrSettings _working;

    public AtrSettingsPopup(AtrSettings current)
    {
        InitializeComponent();
        _working = current?.Clone() ?? AtrSettings.CreateDefaults();
        LoadFields(_working);
    }

    private void LoadFields(AtrSettings settings)
    {
        AtrPeriodEntry.Text = AtrSettings.NormalizeAtrPeriod(settings.AtrPeriod).ToString(CultureInfo.InvariantCulture);
        ImpulseAtrMultiplierEntry.Text = AtrSettings.NormalizeImpulseAtrMultiplier(settings.ImpulseAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        InitialStopAtrMultiplierEntry.Text = AtrSettings.NormalizeInitialStopAtrMultiplier(settings.InitialStopAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        ProfitLockArmAtrMultiplierEntry.Text = AtrSettings.NormalizeProfitLockArmAtrMultiplier(settings.ProfitLockArmAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        ProfitLockStopAtrMultiplierEntry.Text = AtrSettings.NormalizeProfitLockStopAtrMultiplier(settings.ProfitLockStopAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        TrailingArmAtrMultiplierEntry.Text = AtrSettings.NormalizeTrailingArmAtrMultiplier(settings.TrailingArmAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        TrailingAtrMultiplierEntry.Text = AtrSettings.NormalizeTrailingAtrMultiplier(settings.TrailingAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture);
        ErrorLabel.IsVisible = false;
    }

    private void OnDefaultsClicked(object sender, EventArgs e)
    {
        _working = AtrSettings.CreateDefaults();
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

    private bool TryBuildSettings(out AtrSettings settings, out string error)
    {
        settings = _working.Clone();
        error = string.Empty;

        if (!int.TryParse(AtrPeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var atrPeriod) || atrPeriod < 2 || atrPeriod > 200)
        {
            error = "ATR period must be between 2 and 200.";
            return false;
        }

        if (!double.TryParse(ImpulseAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var impulseAtrMultiplier) || impulseAtrMultiplier <= 0 || impulseAtrMultiplier > 20)
        {
            error = "Impulse ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(InitialStopAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialStopAtrMultiplier) || initialStopAtrMultiplier <= 0 || initialStopAtrMultiplier > 20)
        {
            error = "Initial stop ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(ProfitLockArmAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var profitLockArmAtrMultiplier) || profitLockArmAtrMultiplier <= 0 || profitLockArmAtrMultiplier > 20)
        {
            error = "Profit-lock arm ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(ProfitLockStopAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var profitLockStopAtrMultiplier) || profitLockStopAtrMultiplier < 0 || profitLockStopAtrMultiplier > 20)
        {
            error = "Profit-lock stop ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(TrailingArmAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingArmAtrMultiplier) || trailingArmAtrMultiplier <= 0 || trailingArmAtrMultiplier > 20)
        {
            error = "Trailing arm ATR multiplier must be between 0 and 20.";
            return false;
        }

        if (!double.TryParse(TrailingAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingAtrMultiplier) || trailingAtrMultiplier <= 0 || trailingAtrMultiplier > 20)
        {
            error = "Trailing ATR multiplier must be between 0 and 20.";
            return false;
        }

        settings.AtrPeriod = atrPeriod;
        settings.ImpulseAtrMultiplier = impulseAtrMultiplier;
        settings.InitialStopAtrMultiplier = initialStopAtrMultiplier;
        settings.ProfitLockArmAtrMultiplier = profitLockArmAtrMultiplier;
        settings.ProfitLockStopAtrMultiplier = profitLockStopAtrMultiplier;
        settings.TrailingArmAtrMultiplier = trailingArmAtrMultiplier;
        settings.TrailingAtrMultiplier = trailingAtrMultiplier;

        return true;
    }
}
