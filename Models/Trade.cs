using SQLite;

namespace MarketScanner.Models;

public enum TradeStatus
{
    Open,
    Closed
}

[Table("trades")]
public class Trade
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull, MaxLength(20)]
    public string Symbol { get; set; } = string.Empty;

    [NotNull]
    public decimal EntryPrice { get; set; }

    public decimal? ExitPrice { get; set; }

    [NotNull]
    public int Quantity { get; set; }

    [NotNull]
    public decimal ProfitLoss { get; set; }

    [NotNull]
    public decimal ProfitLossPercent { get; set; }

    [NotNull]
    public DateTime EntryTime { get; set; }

    public DateTime? ExitTime { get; set; }

    [NotNull]
    public TradeStatus Status { get; set; } = TradeStatus.Open;

    [MaxLength(100)]
    public string AlgorithmName { get; set; } = string.Empty;

    public decimal? CurrentPrice { get; set; }

    public double? PeakRsiValue { get; set; }
    
    /// <summary>
    /// Highest price reached since position opened (for trailing stop calculation)
    /// </summary>
    public decimal? HighestPrice { get; set; }
    
    /// <summary>
    /// Initial stop-loss price set when position opens
    /// </summary>
    public decimal? InitialStopLossPrice { get; set; }
    
    /// <summary>
    /// Activation price for trailing stop (trailing only activates after price reaches this level)
    /// </summary>
    public decimal? TrailingStopActivationPrice { get; set; }
    
    /// <summary>
    /// Whether trailing stop has been activated (price reached activation level)
    /// </summary>
    public bool TrailingStopActivated { get; set; }
    
    /// <summary>
    /// Current trailing stop price (for tracking - never moves backward)
    /// </summary>
    public decimal? TrailingStopPrice { get; set; }
}

