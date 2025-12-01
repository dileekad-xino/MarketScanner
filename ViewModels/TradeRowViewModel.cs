using CommunityToolkit.Mvvm.ComponentModel;
using MarketScanner.Models;

namespace MarketScanner.ViewModels;

public partial class TradeRowViewModel : ObservableObject
{
    [ObservableProperty] private string _symbol = string.Empty;
    [ObservableProperty] private decimal _entryPrice;
    [ObservableProperty] private decimal _exitPrice;
    [ObservableProperty] private int _quantity;
    [ObservableProperty] private decimal _profitLoss;
    [ObservableProperty] private decimal _profitLossPercent;
    [ObservableProperty] private DateTime _entryTime;
    [ObservableProperty] private DateTime _exitTime;
    [ObservableProperty] private string _algorithmName = string.Empty;

    // Formatted display properties
    public string EntryTimeFormatted => EntryTime.ToString("HH:mm:ss");
    public string ExitTimeFormatted => ExitTime.ToString("HH:mm:ss");
    public string ProfitLossFormatted => ProfitLoss.ToString("C2");
    public string ProfitLossPercentFormatted => $"{ProfitLossPercent:F2}%";
    public string EntryPriceFormatted => EntryPrice.ToString("C2");
    public string ExitPriceFormatted => ExitPrice.ToString("C2");

    // Color properties for UI binding
    public bool IsProfit => ProfitLoss >= 0;
    public bool IsLoss => ProfitLoss < 0;

    public static TradeRowViewModel FromTrade(Trade trade)
    {
        return new TradeRowViewModel
        {
            Symbol = trade.Symbol,
            EntryPrice = trade.EntryPrice,
            ExitPrice = trade.ExitPrice,
            Quantity = trade.Quantity,
            ProfitLoss = trade.ProfitLoss,
            ProfitLossPercent = trade.ProfitLossPercent,
            EntryTime = trade.EntryTime,
            ExitTime = trade.ExitTime,
            AlgorithmName = trade.AlgorithmName
        };
    }
}

