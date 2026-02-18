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
    private Entry? _cciOversoldEntry;
    private Entry? _cciOverboughtEntry;
    private Entry? _cciDaysEntry;
    private Entry? _cciSellZoneMinEntry;
    private Entry? _cciSellZoneMaxEntry;
    private Entry? _cciZoneGapEntry;

    public AlgoRunnerSettingsPopup(string symbol, RsiSettings rsiSettings, CciSettings cciSettings)
    {
        InitializeComponent();
        _rsiWorking = rsiSettings?.Clone() ?? RsiSettings.CreateDefaults();
        _cciWorking = cciSettings?.Clone() ?? CciSettings.CreateDefaults();
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

        Close(new AlgoRunnerSettingsResult(_rsiWorking.Clone(), _cciWorking.Clone()));
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
        _cciPeriodEntry = CreateNumericEntry(_cciWorking.Period.ToString(CultureInfo.InvariantCulture));
        _cciOversoldEntry = CreateNumericEntry(_cciWorking.Oversold.ToString("0.##", CultureInfo.InvariantCulture));
        _cciOverboughtEntry = CreateNumericEntry(_cciWorking.Overbought.ToString("0.##", CultureInfo.InvariantCulture));
        _cciDaysEntry = CreateNumericEntry(_cciWorking.HistoricalDays.ToString(CultureInfo.InvariantCulture));
        _cciSellZoneMinEntry = CreateNumericEntry(_cciWorking.SellZoneMin.ToString(CultureInfo.InvariantCulture));
        _cciSellZoneMaxEntry = CreateNumericEntry(_cciWorking.SellZoneMax.ToString(CultureInfo.InvariantCulture));
        _cciZoneGapEntry = CreateNumericEntry(_cciWorking.ZoneGapInterval.ToString(CultureInfo.InvariantCulture));

        return new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    CreateTwoColumnRow("Length", _cciPeriodEntry),
                    CreateTwoColumnRow("Oversold", _cciOversoldEntry),
                    CreateTwoColumnRow("Overbought", _cciOverboughtEntry),
                    CreateTwoColumnRow("Historical Days", _cciDaysEntry),
                    CreateTwoColumnRow("Sell Zone Min", _cciSellZoneMinEntry),
                    CreateTwoColumnRow("Sell Zone Max", _cciSellZoneMaxEntry),
                    CreateTwoColumnRow("Zone Gap Interval", _cciZoneGapEntry),
                    new Label
                    {
                        Text = "Gap must evenly divide (Sell Zone Max - Sell Zone Min).",
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
        if (_cciPeriodEntry == null || _cciOversoldEntry == null || _cciOverboughtEntry == null || _cciDaysEntry == null ||
            _cciSellZoneMinEntry == null || _cciSellZoneMaxEntry == null || _cciZoneGapEntry == null)
        {
            return "CCI tab is not initialized.";
        }

        if (!int.TryParse(_cciPeriodEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) || period < 2 || period > 200)
            return "CCI length must be between 2 and 200.";

        if (!double.TryParse(_cciOversoldEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var oversold) || oversold < -400 || oversold >= 0)
            return "CCI oversold must be between -400 and 0.";

        if (!double.TryParse(_cciOverboughtEntry.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var overbought) || overbought <= 0 || overbought > 400)
            return "CCI overbought must be between 0 and 400.";

        if (overbought <= oversold)
            return "CCI overbought must be greater than oversold.";

        if (!int.TryParse(_cciDaysEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 1 || days > 60)
            return "CCI historical days must be between 1 and 60.";

        if (!int.TryParse(_cciSellZoneMinEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sellZoneMin) ||
            !CciSettings.IsValidSellZoneMin(sellZoneMin))
            return "Sell zone min must be greater than 0.";

        if (!int.TryParse(_cciSellZoneMaxEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sellZoneMax) ||
            !CciSettings.IsValidSellZoneMax(sellZoneMax))
            return "Sell zone max must be greater than 0.";

        if (!CciSettings.IsValidSellZoneBounds(sellZoneMin, sellZoneMax))
            return "Sell zone max must be greater than sell zone min.";

        if (!int.TryParse(_cciZoneGapEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gap) ||
            !CciSettings.IsValidZoneGapInterval(gap, sellZoneMin, sellZoneMax))
            return $"Zone gap must evenly divide {CciSettings.GetSellZoneRange(sellZoneMin, sellZoneMax)} (max - min).";

        _cciWorking.Period = period;
        _cciWorking.Oversold = oversold;
        _cciWorking.Overbought = overbought;
        _cciWorking.HistoricalDays = days;
        _cciWorking.SellZoneMin = sellZoneMin;
        _cciWorking.SellZoneMax = sellZoneMax;
        _cciWorking.ZoneGapInterval = gap;
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
