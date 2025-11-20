# RSI Implementation Guide for IBKR API Algorithms

## Overview

The IBKR TWS API does **not** provide RSI (Relative Strength Index) directly. You must:
1. Request historical price data via `reqHistoricalData`
2. Calculate RSI from the historical close prices
3. Use RSI values in your trading algorithm

## Understanding IBKR Historical Data API

### Current Implementation in MarketScanner

The `IbkrGatewayService` already requests historical data for average volume calculation:

```csharp
_client.reqHistoricalData(
    reqId, contract, "", "30 D", "1 day", "TRADES", 1, 1, false, null);
```

**Parameters:**
- `reqId`: Unique request identifier
- `contract`: Contract object (symbol, secType, exchange, currency)
- `endDateTime`: End date/time (empty = now)
- `durationStr`: "30 D" = 30 days of data
- `barSizeSetting`: "1 day" = daily bars
- `whatToShow`: "TRADES" = trade data
- `useRTH`: 1 = regular trading hours only
- `formatDate`: 1 = string format
- `keepUpToDate`: false = snapshot (true = streaming updates)
- `chartOptions`: null = no additional options

### Requesting Historical Data for RSI

For RSI calculation, you typically need **14-30 periods** of daily data. Here's how to request it:

```csharp
// Request 30 days of daily bars for RSI calculation (14-period RSI + buffer)
_client.reqHistoricalData(
    reqId, 
    contract, 
    "",                    // endDateTime: empty = current time
    "30 D",                // duration: 30 days
    "1 day",               // barSize: daily bars
    "TRADES",              // whatToShow: trade data
    1,                     // useRTH: regular trading hours
    1,                     // formatDate: string format
    false,                 // keepUpToDate: snapshot only
    null                    // chartOptions
);
```

## RSI Calculation Formula

RSI is calculated using the following formula:

```
RSI = 100 - (100 / (1 + RS))
where RS = Average Gain / Average Loss

For 14-period RSI:
- Average Gain = Sum of gains over 14 periods / 14
- Average Loss = Sum of losses over 14 periods / 14
```

### Standard RSI Calculation Steps

1. **Calculate price changes**: For each period, calculate Close(t) - Close(t-1)
2. **Separate gains and losses**: 
   - Gain = positive change (or 0)
   - Loss = absolute value of negative change (or 0)
3. **Calculate initial averages** (first 14 periods):
   - Avg Gain = Sum of first 14 gains / 14
   - Avg Loss = Sum of first 14 losses / 14
4. **Calculate smoothed averages** (subsequent periods):
   - Avg Gain = [(Previous Avg Gain × 13) + Current Gain] / 14
   - Avg Loss = [(Previous Avg Loss × 13) + Current Loss] / 14
5. **Calculate RS and RSI**:
   - RS = Avg Gain / Avg Loss
   - RSI = 100 - (100 / (1 + RS))

## Implementation Example

### Step 1: Create RSI Calculator Utility

```csharp
// Utilities/RSICalculator.cs
namespace MarketScanner.Utilities;

public static class RSICalculator
{
    /// <summary>
    /// Calculates RSI from historical close prices.
    /// </summary>
    /// <param name="closePrices">Array of close prices (oldest to newest)</param>
    /// <param name="period">RSI period (default 14)</param>
    /// <returns>RSI value (0-100)</returns>
    public static double Calculate(double[] closePrices, int period = 14)
    {
        if (closePrices.Length < period + 1)
            throw new ArgumentException($"Need at least {period + 1} data points for {period}-period RSI");

        // Calculate price changes
        var changes = new double[closePrices.Length - 1];
        for (int i = 0; i < changes.Length; i++)
        {
            changes[i] = closePrices[i + 1] - closePrices[i];
        }

        // Separate gains and losses
        var gains = new double[changes.Length];
        var losses = new double[changes.Length];
        for (int i = 0; i < changes.Length; i++)
        {
            gains[i] = changes[i] > 0 ? changes[i] : 0;
            losses[i] = changes[i] < 0 ? Math.Abs(changes[i]) : 0;
        }

        // Calculate initial average gain and loss (first period values)
        double avgGain = gains.Take(period).Average();
        double avgLoss = losses.Take(period).Average();

        // Calculate smoothed averages for remaining periods
        for (int i = period; i < changes.Length; i++)
        {
            avgGain = ((avgGain * (period - 1)) + gains[i]) / period;
            avgLoss = ((avgLoss * (period - 1)) + losses[i]) / period;
        }

        // Calculate RS and RSI
        if (avgLoss == 0) return 100; // Avoid division by zero
        double rs = avgGain / avgLoss;
        double rsi = 100 - (100 / (1 + rs));

        return rsi;
    }
}
```

### Step 2: Extend IbkrGatewayService for RSI Data

Add a method to request historical data specifically for RSI:

```csharp
// In Services/Ibkr/IbkrGatewayService.cs

/// <summary>
/// Requests historical data for RSI calculation.
/// </summary>
public async Task<List<Bar>> GetHistoricalBarsForRSIAsync(
    string symbol, 
    int days = 30, 
    CancellationToken ct = default)
{
    await EnsureConnectedAsync(ct);

    var reqId = GetNextReqId();
    var tcs = new TaskCompletionSource<List<Bar>>();
    var bars = new List<Bar>();

    // Store request mapping
    _histReqToSymbol[reqId] = symbol;
    
    // Create a temporary dictionary to store bars for this request
    var barsDict = new ConcurrentDictionary<int, List<Bar>>();
    barsDict[reqId] = bars;

    var contract = new Contract
    {
        Symbol = symbol,
        SecType = "STK",
        Exchange = "SMART",
        Currency = "USD"
    };

    try
    {
        // Request historical data
        _client.reqHistoricalData(
            reqId,
            contract,
            "",                    // endDateTime
            $"{days} D",           // duration
            "1 day",               // barSize
            "TRADES",              // whatToShow
            1,                     // useRTH
            1,                     // formatDate
            false,                 // keepUpToDate
            null                    // chartOptions
        );

        // Wait for historicalDataEnd callback
        // Note: You'll need to modify historicalData callback to store bars
        // and historicalDataEnd to complete the TaskCompletionSource
        
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        
        // Implementation would need callback handling - see Step 3
        await tcs.Task.WaitAsync(linkedCts.Token);
        
        return bars;
    }
    catch (OperationCanceledException)
    {
        _logger.LogWarning("Historical data request timed out for {Symbol}", symbol);
        throw new TimeoutException($"Historical data request timed out for {symbol}");
    }
    finally
    {
        _histReqToSymbol.TryRemove(reqId, out _);
        barsDict.TryRemove(reqId, out _);
    }
}
```

### Step 3: Create RSI-Based Algorithm

```csharp
// Services/Impl/RSIAlgoStrategy.cs
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;

namespace MarketScanner.Services.Impl;

public class RSIAlgoStrategy : IAlgoStrategy
{
    private readonly IbkrGatewayService _ibkrService;
    private readonly ILogger<RSIAlgoStrategy> _logger;
    private const int RSI_PERIOD = 14;
    private const double RSI_OVERSOLD = 30.0;  // Buy signal
    private const double RSI_OVERBOUGHT = 70.0; // Sell signal

    public string Name => "RSI Strategy";
    public string Description => "Trading strategy based on Relative Strength Index (RSI)";

    public RSIAlgoStrategy(
        IbkrGatewayService ibkrService,
        ILogger<RSIAlgoStrategy> logger)
    {
        _ibkrService = ibkrService;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            // Step 1: Get historical data (you'll need to implement GetHistoricalBarsForRSIAsync)
            // For now, using a simplified approach with existing data
            
            // Step 2: Extract close prices from historical bars
            // Note: This requires extending IbkrGatewayService to return historical bars
            // For demonstration, we'll use a placeholder
            
            // Placeholder: In real implementation, get historical bars
            // var bars = await _ibkrService.GetHistoricalBarsForRSIAsync(symbol.Symbol, 30, ct);
            // var closePrices = bars.Select(b => (double)b.Close).Reverse().ToArray();
            
            // Simplified: Use current price as placeholder
            // In production, you MUST get actual historical data
            _logger.LogWarning("RSI calculation requires historical data - using placeholder");
            
            // Step 3: Calculate RSI
            // double rsi = RSICalculator.Calculate(closePrices, RSI_PERIOD);
            
            // Placeholder RSI (replace with actual calculation)
            double rsi = 50.0; // This should be calculated from historical data
            
            // Step 4: Generate trading signal
            AlgoAction action;
            string reason;

            if (rsi < RSI_OVERSOLD)
            {
                action = AlgoAction.Buy;
                reason = $"RSI ({rsi:F2}) is oversold (< {RSI_OVERSOLD}) - potential buy signal";
            }
            else if (rsi > RSI_OVERBOUGHT)
            {
                action = AlgoAction.Sell;
                reason = $"RSI ({rsi:F2}) is overbought (> {RSI_OVERBOUGHT}) - potential sell signal";
            }
            else
            {
                action = AlgoAction.Hold;
                reason = $"RSI ({rsi:F2}) is neutral ({RSI_OVERSOLD}-{RSI_OVERBOUGHT}) - hold position";
            }

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing RSI algorithm for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"Error: {ex.Message}",
                Timestamp: DateTime.UtcNow
            );
        }
    }
}
```

## Key Points from IBKR API Documentation

According to the [IBKR TWS API documentation](https://www.interactivebrokers.com/campus/ibkr-api-page/twsapi-doc/):

1. **Market Data Subscriptions**: You need appropriate market data subscriptions to access historical data
2. **Rate Limiting**: IBKR enforces pacing limitations on historical data requests
3. **Data Format**: Historical data comes as `Bar` objects with Open, High, Low, Close, Volume
4. **Callback Pattern**: Historical data is received via `historicalData()` and `historicalDataEnd()` callbacks

## Best Practices

1. **Cache Historical Data**: Don't request the same data repeatedly
2. **Respect Rate Limits**: IBKR has pacing limitations - space out requests
3. **Error Handling**: Always handle timeouts and connection errors
4. **Data Validation**: Ensure you have enough data points before calculating RSI
5. **Period Selection**: Standard RSI uses 14 periods, but you can adjust based on your strategy

## Next Steps

1. Implement `GetHistoricalBarsForRSIAsync()` in `IbkrGatewayService`
2. Add RSI calculator utility
3. Create RSI-based algorithm strategy
4. Register the new algorithm in `MauiProgram.cs`
5. Test with paper trading account first

## References

- [IBKR TWS API Documentation](https://www.interactivebrokers.com/campus/ibkr-api-page/twsapi-doc/)
- [IBKR API Market Data Subscriptions](https://www.interactivebrokers.com/en/index.php?f=marketData)
- [RSI Indicator Explanation](https://www.investopedia.com/terms/r/rsi.asp)

