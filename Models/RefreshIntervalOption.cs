namespace MarketScanner.Models;

public sealed class RefreshIntervalOption
{
    public int Seconds { get; }
    public string Label => $"{Seconds}s";

    public RefreshIntervalOption(int seconds)
    {
        Seconds = seconds;
    }

    public override string ToString() => Label;
}
