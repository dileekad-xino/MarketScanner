using System.Text.Json.Serialization;

namespace MarketScanner.Models;

public sealed record ChartCandlePoint
{
    [JsonPropertyName("time")]
    public long Time { get; init; }

    [JsonPropertyName("open")]
    public double Open { get; init; }

    [JsonPropertyName("high")]
    public double High { get; init; }

    [JsonPropertyName("low")]
    public double Low { get; init; }

    [JsonPropertyName("close")]
    public double Close { get; init; }

    [JsonPropertyName("volume")]
    public double? Volume { get; init; }
}

public sealed record ChartLinePoint
{
    [JsonPropertyName("time")]
    public long Time { get; init; }

    [JsonPropertyName("value")]
    public double Value { get; init; }
}

public sealed class ChartSnapshot
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("candles")]
    public IReadOnlyList<ChartCandlePoint> Candles { get; init; } = Array.Empty<ChartCandlePoint>();

    [JsonPropertyName("movingAverage")]
    public IReadOnlyList<ChartLinePoint>? MovingAverage { get; init; }
}

