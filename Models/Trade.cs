using SQLite;

namespace MarketScanner.Models;

[Table("trades")]
public class Trade
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed, NotNull, MaxLength(20)]
    public string Symbol { get; set; } = string.Empty;

    [NotNull]
    public decimal EntryPrice { get; set; }

    [NotNull]
    public decimal ExitPrice { get; set; }

    [NotNull]
    public int Quantity { get; set; }

    [NotNull]
    public decimal ProfitLoss { get; set; }

    [NotNull]
    public decimal ProfitLossPercent { get; set; }

    [NotNull]
    public DateTime EntryTime { get; set; }

    [NotNull]
    public DateTime ExitTime { get; set; }

    [MaxLength(100)]
    public string AlgorithmName { get; set; } = string.Empty;
}

