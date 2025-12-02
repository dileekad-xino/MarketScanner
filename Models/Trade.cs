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
}

