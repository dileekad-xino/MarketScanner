using CommunityToolkit.Maui.Views;
using MarketScanner.Models;
using System.Globalization;

namespace MarketScanner.Views.Dialogs;

public partial class AlgoRunnerSettingsPopup : Popup
{
    private readonly Dictionary<string, SettingsTabDefinition> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedTabKey = string.Empty;
    private RsiSettings _rsiWorking;
    private CciSettings _cciWorking;
    private AtrSettings _atrWorking;

    // RSI tab fields
    private Entry? _rsiPeriodEntry;
    private Entry? _rsiOversoldEntry;
    private Entry? _rsiOverboughtEntry;
    private Entry? _rsiDaysEntry;
    private Entry? _initialStopLossEntry;
    private Picker? _trailingStopModePicker;
    private Entry? _trailingStopDistanceEntry;
    private Label? _trailingStopUnitLabel;
    private Entry? _activationPriceEntry;

    // CCI tab fields
    private Entry? _cciPeriodEntry;
    private Entry? _entryThresholdEntry;
    private Entry? _entryMinDeltaEntry;
    private Switch? _requireRisingEma20Switch;
    private Entry? _cciDaysEntry;

    // ATR tab fields
    private Entry? _atrPeriodEntry;
    private Entry? _impulseAtrMultiplierEntry;
    private Entry? _initialStopAtrMultiplierEntry;
    private Entry? _profitLockArmAtrMultiplierEntry;
    private Entry? _profitLockStopAtrMultiplierEntry;
    private Entry? _trailingArmAtrMultiplierEntry;
    private Entry? _trailingAtrMultiplierEntry;

    public AlgoRunnerSettingsPopup(string symbol, RsiSettings rsiSettings, CciSettings cciSettings, AtrSettings atrSettings)
    {
        InitializeComponent();
        _rsiWorking = rsiSettings?.Clone() ?? RsiSettings.CreateDefaults();
        _cciWorking = cciSettings?.Clone() ?? CciSettings.CreateDefaults();
        _atrWorking = atrSettings?.Clone() ?? AtrSettings.CreateDefaults();
        TitleLabel.Text = $"Algo Runner Settings - {symbol}";

        RegisterTab(new SettingsTabDefinition(
            "rsi",
            "RSI Settings",
            BuildRsiTab,
            ValidateAndApplyRsiTab,
            ResetRsiDefaults));

        RegisterTab(new SettingsTabDefinition(
            "cci",
            "CCI Settings",
            BuildCciTab,
            ValidateAndApplyCciTab,
            ResetCciDefaults));

        RegisterTab(new SettingsTabDefinition(
            "atr",
            "ATR Settings",
            BuildAtrTab,
            ValidateAndApplyAtrTab,
            ResetAtrDefaults));

        RenderTabHeaders();
        SwitchToTab("rsi");
    }

    private void RegisterTab(SettingsTabDefinition tab)
    {
        _tabs[tab.Key] = tab;
    }

    private void RenderTabHeaders()
    {
        TabHeaderHost.Children.Clear();
        foreach (var tab in _tabs.Values)
        {
            var button = new Button
            {
                Text = tab.Title,
                HeightRequest = 34,
                Padding = new Thickness(12, 0),
                CornerRadius = 6
            };
            button.Clicked += (_, _) => SwitchToTab(tab.Key);
            tab.HeaderButton = button;
            TabHeaderHost.Children.Add(button);
        }
        RefreshTabHeaderStyles();
    }

    private void SwitchToTab(string key)
    {
        if (!_tabs.TryGetValue(key, out var tab))
            return;

        _selectedTabKey = key;
        tab.CachedContent ??= tab.ContentFactory();
        TabContentHost.Content = tab.CachedContent;
        ErrorLabel.IsVisible = false;
        RefreshTabHeaderStyles();
    }

    private void RefreshTabHeaderStyles()
    {
        foreach (var tab in _tabs.Values)
        {
            if (tab.HeaderButton == null)
                continue;

            bool selected = string.Equals(tab.Key, _selectedTabKey, StringComparison.OrdinalIgnoreCase);
            tab.HeaderButton.BackgroundColor = selected
                ? Color.FromArgb("#2A6A5C")
                : Color.FromArgb("#2B2B2B");
            tab.HeaderButton.TextColor = Colors.White;
            tab.HeaderButton.BorderColor = selected
                ? Color.FromArgb("#5FC9AF")
                : Color.FromArgb("#4A4A4A");
            tab.HeaderButton.BorderWidth = 1;
        }
    }

    private void OnDefaultsClicked(object sender, EventArgs e)
    {
        if (_tabs.TryGetValue(_selectedTabKey, out var tab))
        {
            tab.ResetToDefaults();
            tab.CachedContent = tab.ContentFactory();
            TabContentHost.Content = tab.CachedContent;
            ErrorLabel.IsVisible = false;
        }
    }

    private void OnCancelClicked(object sender, EventArgs e)
    {
        Close(null);
    }

    private void OnOkClicked(object sender, EventArgs e)
    {
        foreach (var tab in _tabs.Values)
        {
            var error = tab.ValidateAndApply();
            if (!string.IsNullOrWhiteSpace(error))
            {
                SwitchToTab(tab.Key);
                ErrorLabel.Text = error;
                ErrorLabel.IsVisible = true;
                return;
            }
        }

        Close(new AlgoRunnerSettingsResult(_rsiWorking.Clone(), _cciWorking.Clone(), _atrWorking.Clone()));
    }

    private View BuildRsiTab()
    {
        _rsiPeriodEntry = CreateNumericEntry(_rsiWorking.Period.ToString(CultureInfo.InvariantCulture));
        _rsiOversoldEntry = CreateNumericEntry(_rsiWorking.Oversold.ToString("0.##", CultureInfo.InvariantCulture));
        _rsiOverboughtEntry = CreateNumericEntry(_rsiWorking.Overbought.ToString("0.##", CultureInfo.InvariantCulture));
        _rsiDaysEntry = CreateNumericEntry(_rsiWorking.HistoricalDays.ToString(CultureInfo.InvariantCulture));
        _initialStopLossEntry = CreateNumericEntry(_rsiWorking.InitialStopLossPercent.ToString("0.##", CultureInfo.InvariantCulture));
        _trailingStopDistanceEntry = CreateNumericEntry(_rsiWorking.TrailingStopDistance.ToString("0.##", CultureInfo.InvariantCulture));
        _activationPriceEntry = CreateNumericEntry(_rsiWorking.TrailingStopActivationPercent.ToString("0.##", CultureInfo.InvariantCulture));
        _trailingStopModePicker = new Picker
        {
            ItemsSource = new List<string> { "Percentage", "Price" },
            SelectedIndex = _rsiWorking.TrailingStopMode == TrailingStopMode.Percentage ? 0 : 1
        };
        _trailingStopUnitLabel = new Label { VerticalOptions = LayoutOptions.Center, TextColor = Colors.White, Text = "%" };
        _trailingStopModePicker.SelectedIndexChanged += (_, _) => UpdateTrailingStopUnitLabel();
        UpdateTrailingStopUnitLabel();

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    CreateTwoColumnRow("Length", _rsiPeriodEntry),
                    CreateTwoColumnRow("Oversold", _rsiOversoldEntry),
                    CreateTwoColumnRow("Overbought", _rsiOverboughtEntry),
                    CreateTwoColumnRow("Historical Days", _rsiDaysEntry),
                    CreateThreeColumnRow("Initial Stop-Loss", _initialStopLossEntry, new Label { Text = "%", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center }),
                    CreateTwoColumnRow("Trailing Stop Mode", _trailingStopModePicker),
                    CreateThreeColumnRow("Trailing Distance", _trailingStopDistanceEntry, _trailingStopUnitLabel),
                    CreateThreeColumnRow("Activation Price", _activationPriceEntry, new Label { Text = "%", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center })
                }
            }
        };
    }

    private View BuildCciTab()
    {
        _cciPeriodEntry = CreateNumericEntry(CciSettings.NormalizePeriod(_cciWorking.Period).ToString(CultureInfo.InvariantCulture));
        _entryThresholdEntry = CreateNumericEntry(CciSettings.NormalizeEntryThreshold(_cciWorking.EntryThreshold).ToString("0.##", CultureInfo.InvariantCulture));
        _entryMinDeltaEntry = CreateNumericEntry(CciSettings.NormalizeEntryMinDelta(_cciWorking.EntryMinDelta).ToString("0.##", CultureInfo.InvariantCulture));
        _requireRisingEma20Switch = new Switch { IsToggled = _cciWorking.RequireRisingEma20, HorizontalOptions = LayoutOptions.Start };
        _cciDaysEntry = CreateNumericEntry(_cciWorking.HistoricalDays.ToString(CultureInfo.InvariantCulture));

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    CreateTwoColumnRow("CCI Period", _cciPeriodEntry),
                    CreateTwoColumnRow("Entry Threshold", _entryThresholdEntry),
                    CreateTwoColumnRow("Entry Min Delta", _entryMinDeltaEntry),
                    CreateTwoColumnRow("Require Rising EMA20", _requireRisingEma20Switch),
                    CreateTwoColumnRow("Historical Days", _cciDaysEntry)
                }
            }
        };
    }

    private View BuildAtrTab()
    {
        _atrPeriodEntry = CreateNumericEntry(AtrSettings.NormalizeAtrPeriod(_atrWorking.AtrPeriod).ToString(CultureInfo.InvariantCulture));
        _impulseAtrMultiplierEntry = CreateNumericEntry(AtrSettings.NormalizeImpulseAtrMultiplier(_atrWorking.ImpulseAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture));
        _initialStopAtrMultiplierEntry = CreateNumericEntry(AtrSettings.NormalizeInitialStopAtrMultiplier(_atrWorking.InitialStopAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture));
        _profitLockArmAtrMultiplierEntry = CreateNumericEntry(AtrSettings.NormalizeProfitLockArmAtrMultiplier(_atrWorking.ProfitLockArmAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture));
        _profitLockStopAtrMultiplierEntry = CreateNumericEntry(AtrSettings.NormalizeProfitLockStopAtrMultiplier(_atrWorking.ProfitLockStopAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture));
        _trailingArmAtrMultiplierEntry = CreateNumericEntry(AtrSettings.NormalizeTrailingArmAtrMultiplier(_atrWorking.TrailingArmAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture));
        _trailingAtrMultiplierEntry = CreateNumericEntry(AtrSettings.NormalizeTrailingAtrMultiplier(_atrWorking.TrailingAtrMultiplier).ToString("0.##", CultureInfo.InvariantCulture));

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    CreateTwoColumnRow("ATR Period", _atrPeriodEntry),
                    CreateTwoColumnRow("Impulse ATR Mult", _impulseAtrMultiplierEntry),
                    CreateTwoColumnRow("Initial Stop ATR Mult", _initialStopAtrMultiplierEntry),
                    CreateTwoColumnRow("Profit Lock Arm ATR Mult", _profitLockArmAtrMultiplierEntry),
                    CreateTwoColumnRow("Profit Lock Stop ATR Mult", _profitLockStopAtrMultiplierEntry),
                    CreateTwoColumnRow("Trailing Arm ATR Mult", _trailingArmAtrMultiplierEntry),
                    CreateTwoColumnRow("Trailing ATR Mult", _trailingAtrMultiplierEntry),
                    new Label
                    {
                        Text = "3-stage stops use Entry ATR frozen at BUY: initial, profit lock, then full trailing (armed by highest price progress).",
                        TextColor = Color.FromArgb("#B0B0B0"),
                        FontSize = 12
                    }
                }
            }
        };
    }

    private string? ValidateAndApplyRsiTab()
    {
        if (_rsiPeriodEntry == null || _rsiOversoldEntry == null || _rsiOverboughtEntry == null || _rsiDaysEntry == null ||
            _initialStopLossEntry == null || _trailingStopModePicker == null || _trailingStopDistanceEntry == null || _activationPriceEntry == null)
        {
            return "RSI tab is not initialized.";
        }

        if (!int.TryParse(_rsiPeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) || period < 2 || period > 200)
            return "RSI length must be between 2 and 200.";

        if (!double.TryParse(_rsiOversoldEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var oversold) || oversold <= 0 || oversold >= 50)
            return "RSI oversold must be between 0 and 50.";

        if (!double.TryParse(_rsiOverboughtEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var overbought) || overbought <= 50 || overbought >= 100)
            return "RSI overbought must be between 50 and 100.";

        if (!int.TryParse(_rsiDaysEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 60)
            return "RSI historical days must be between 1 and 60.";

        if (!double.TryParse(_initialStopLossEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialStopLoss) || initialStopLoss <= 0 || initialStopLoss > 10)
            return "Initial stop-loss must be between 0.1 and 10 percent.";

        if (_trailingStopModePicker.SelectedIndex < 0)
            return "Select trailing stop mode.";

        if (!double.TryParse(_trailingStopDistanceEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingDistance))
            return "Trailing distance must be a valid number.";

        var trailingStopMode = _trailingStopModePicker.SelectedIndex == 0 ? TrailingStopMode.Percentage : TrailingStopMode.Price;
        if (trailingStopMode == TrailingStopMode.Percentage && (trailingDistance <= 0 || trailingDistance > 10))
            return "Trailing stop percentage must be between 0.1 and 10.";

        if (trailingStopMode == TrailingStopMode.Price && (trailingDistance <= 0 || trailingDistance > 100))
            return "Trailing stop price distance must be between $0.01 and $100.00.";

        if (!double.TryParse(_activationPriceEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var activationPrice) || activationPrice <= 0 || activationPrice > 20)
            return "Activation price must be between 0.1 and 20 percent.";

        _rsiWorking.Period = period;
        _rsiWorking.Oversold = oversold;
        _rsiWorking.Overbought = overbought;
        _rsiWorking.HistoricalDays = days;
        _rsiWorking.InitialStopLossPercent = initialStopLoss;
        _rsiWorking.TrailingStopMode = trailingStopMode;
        _rsiWorking.TrailingStopDistance = trailingDistance;
        _rsiWorking.TrailingStopActivationPercent = activationPrice;
        _rsiWorking.TrailingStopPoints = trailingDistance;
        return null;
    }

    private string? ValidateAndApplyCciTab()
    {
        if (_cciPeriodEntry == null || _entryThresholdEntry == null || _entryMinDeltaEntry == null || _requireRisingEma20Switch == null || _cciDaysEntry == null)
            return "CCI tab is not initialized.";

        if (!int.TryParse(_cciPeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) || period < 2 || period > 200)
            return "CCI period must be between 2 and 200.";

        if (!double.TryParse(_entryThresholdEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var entryThreshold) || entryThreshold <= 0 || entryThreshold > 400)
            return "Entry threshold must be between 0 and 400.";

        if (!double.TryParse(_entryMinDeltaEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var entryMinDelta) || entryMinDelta < 0 || entryMinDelta > 200)
            return "Entry min delta must be between 0 and 200.";

        if (!int.TryParse(_cciDaysEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 60)
            return "CCI historical days must be between 1 and 60.";

        _cciWorking.Period = period;
        _cciWorking.EntryThreshold = entryThreshold;
        _cciWorking.EntryMinDelta = entryMinDelta;
        _cciWorking.RequireRisingEma20 = _requireRisingEma20Switch.IsToggled;
        _cciWorking.HistoricalDays = days;
        return null;
    }

    private string? ValidateAndApplyAtrTab()
    {
        if (_atrPeriodEntry == null || _impulseAtrMultiplierEntry == null || _initialStopAtrMultiplierEntry == null ||
            _profitLockArmAtrMultiplierEntry == null || _profitLockStopAtrMultiplierEntry == null || _trailingArmAtrMultiplierEntry == null || _trailingAtrMultiplierEntry == null)
            return "ATR tab is not initialized.";

        if (!int.TryParse(_atrPeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var atrPeriod) || atrPeriod < 2 || atrPeriod > 200)
            return "ATR period must be between 2 and 200.";

        if (!double.TryParse(_impulseAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var impulseAtrMultiplier) || impulseAtrMultiplier <= 0 || impulseAtrMultiplier > 20)
            return "Impulse ATR multiplier must be between 0 and 20.";

        if (!double.TryParse(_initialStopAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialStopAtrMultiplier) || initialStopAtrMultiplier <= 0 || initialStopAtrMultiplier > 20)
            return "Initial stop ATR multiplier must be between 0 and 20.";

        if (!double.TryParse(_profitLockArmAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var profitLockArmAtrMultiplier) || profitLockArmAtrMultiplier <= 0 || profitLockArmAtrMultiplier > 20)
            return "Profit-lock arm ATR multiplier must be between 0 and 20.";

        if (!double.TryParse(_profitLockStopAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var profitLockStopAtrMultiplier) || profitLockStopAtrMultiplier < 0 || profitLockStopAtrMultiplier > 20)
            return "Profit-lock stop ATR multiplier must be between 0 and 20.";

        if (!double.TryParse(_trailingArmAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingArmAtrMultiplier) || trailingArmAtrMultiplier <= 0 || trailingArmAtrMultiplier > 20)
            return "Trailing arm ATR multiplier must be between 0 and 20.";

        if (!double.TryParse(_trailingAtrMultiplierEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var trailingAtrMultiplier) || trailingAtrMultiplier <= 0 || trailingAtrMultiplier > 20)
            return "Trailing ATR multiplier must be between 0 and 20.";

        _atrWorking.AtrPeriod = atrPeriod;
        _atrWorking.ImpulseAtrMultiplier = impulseAtrMultiplier;
        _atrWorking.InitialStopAtrMultiplier = initialStopAtrMultiplier;
        _atrWorking.ProfitLockArmAtrMultiplier = profitLockArmAtrMultiplier;
        _atrWorking.ProfitLockStopAtrMultiplier = profitLockStopAtrMultiplier;
        _atrWorking.TrailingArmAtrMultiplier = trailingArmAtrMultiplier;
        _atrWorking.TrailingAtrMultiplier = trailingAtrMultiplier;
        return null;
    }

    private void ResetRsiDefaults()
    {
        _rsiWorking = RsiSettings.CreateDefaults();
    }

    private void ResetCciDefaults()
    {
        _cciWorking = CciSettings.CreateDefaults();
    }

    private void ResetAtrDefaults()
    {
        _atrWorking = AtrSettings.CreateDefaults();
    }

    private void UpdateTrailingStopUnitLabel()
    {
        if (_trailingStopUnitLabel == null || _trailingStopModePicker == null)
            return;

        _trailingStopUnitLabel.Text = _trailingStopModePicker.SelectedIndex == 0 ? "%" : "$";
    }

    private static Entry CreateNumericEntry(string text) =>
        new()
        {
            Text = text,
            Keyboard = Keyboard.Numeric,
            HorizontalOptions = LayoutOptions.Fill
        };

    private static Grid CreateTwoColumnRow(string labelText, View input)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 14
        };
        var label = new Label
        {
            Text = labelText,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center
        };
        Grid.SetColumn(label, 0);
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        Grid.SetColumn(input, 1);
        Grid.SetRow(input, 0);
        grid.Children.Add(input);
        return grid;
    }

    private static Grid CreateThreeColumnRow(string labelText, View input, View suffix)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 14
        };
        var label = new Label
        {
            Text = labelText,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center
        };
        Grid.SetColumn(label, 0);
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        Grid.SetColumn(input, 1);
        Grid.SetRow(input, 0);
        grid.Children.Add(input);

        Grid.SetColumn(suffix, 2);
        Grid.SetRow(suffix, 0);
        grid.Children.Add(suffix);
        return grid;
    }

    private sealed class SettingsTabDefinition
    {
        public SettingsTabDefinition(
            string key,
            string title,
            Func<View> contentFactory,
            Func<string?> validateAndApply,
            Action resetToDefaults)
        {
            Key = key;
            Title = title;
            ContentFactory = contentFactory;
            ValidateAndApply = validateAndApply;
            ResetToDefaults = resetToDefaults;
        }

        public string Key { get; }
        public string Title { get; }
        public Func<View> ContentFactory { get; }
        public Func<string?> ValidateAndApply { get; }
        public Action ResetToDefaults { get; }
        public View? CachedContent { get; set; }
        public Button? HeaderButton { get; set; }
    }
}
