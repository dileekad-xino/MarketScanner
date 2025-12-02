using CommunityToolkit.Mvvm.ComponentModel;
using MarketScanner.Models;

namespace MarketScanner.ViewModels;

public partial class TradeRowViewModel : ObservableObject
{
    public Trade Trade { get; }

    public TradeRowViewModel(Trade trade)
    {
        Trade = trade;
    }

    // Properties from Trade
    public string Symbol => Trade.Symbol;
    public decimal EntryPrice => Trade.EntryPrice;
    public decimal? ExitPrice => Trade.ExitPrice;
    public int Quantity => Trade.Quantity;
    public decimal ProfitLoss => Trade.ProfitLoss;
    public decimal ProfitLossPercent => Trade.ProfitLossPercent;
    public DateTime EntryTime => Trade.EntryTime;
    public DateTime? ExitTime => Trade.ExitTime;
    public TradeStatus Status => Trade.Status;
    public string AlgorithmName => Trade.AlgorithmName ?? string.Empty;
    public decimal? CurrentPrice => Trade.CurrentPrice;

    // Formatted display properties
    public string EntryTimeFormatted => EntryTime.ToString("HH:mm:ss");
    public string ExitTimeFormatted => ExitTime?.ToString("HH:mm:ss") ?? "—";
    public string ProfitLossFormatted => ProfitLoss.ToString("C2");
    public string ProfitLossPercentFormatted => $"{ProfitLossPercent:F2}%";
    public string EntryPriceFormatted => EntryPrice.ToString("C2");
    public string ExitPriceFormatted => ExitPrice?.ToString("C2") ?? "—";
    public string StatusText => Status == TradeStatus.Open ? "Open" : "Closed";
    public string CurrentPriceFormatted => CurrentPrice?.ToString("C2") ?? "—";

    // Color properties for UI binding
    public bool IsProfit => ProfitLoss >= 0;
    public bool IsLoss => ProfitLoss < 0;
    public bool IsOpen => Status == TradeStatus.Open;
    public bool IsClosed => Status == TradeStatus.Closed;

    public static TradeRowViewModel FromTrade(Trade trade)
    {
        return new TradeRowViewModel(trade);
    }
}

