using IBApi;
using MarketScanner.Models;
using MarketScanner.Services;
using MarketScanner.Services.Ibkr;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace MarketScanner.Services.Impl;

/// <summary>
/// RSI-based trading algorithm strategy.
/// Uses Relative Strength Index (RSI) to generate buy/sell/hold signals.
/// </summary>
public class RSIAlgoStrategy : IAlgoStrategy
{
    private readonly IbkrGatewayService _ibkrService;
    private readonly IRsiSettingsService _settingsService;
    private readonly ILogger<RSIAlgoStrategy> _logger;
    
    private const int TrendEmaPeriod = 20;

    public string Name => "RSI Strategy";
    public string Description => "Trading strategy based on Relative Strength Index (RSI). " +
                               "Buy when RSI < 30 (oversold), Sell when RSI > 70 (overbought).";

    public RSIAlgoStrategy(
        IbkrGatewayService ibkrService,
        IRsiSettingsService settingsService,
        ILogger<RSIAlgoStrategy> logger)
    {
        _ibkrService = ibkrService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(ScannerRowViewModel symbol, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("RSIAlgoStrategy: Executing for symbol {Symbol}", symbol.Symbol);

            var settings = await _settingsService.GetAsync(ct).ConfigureAwait(false);

            // Step 1: Get historical bars from IBKR
            IReadOnlyList<Bar> bars;
            try
            {
                bars = await _ibkrService.GetHistoricalBarsForRSIAsync(
                    symbol.Symbol,
                    days: settings.HistoricalDays,
                    barSize: "1 min",
                    ct: ct);

                if (bars == null || bars.Count == 0)
                {
                    return new AlgoResult(
                        Symbol: symbol.Symbol,
                        Action: AlgoAction.Hold,
                        Price: symbol.LastPrice,
                        Reason: "No historical data available for RSI calculation",
                        Timestamp: DateTime.UtcNow,
                        RsiValue: null,
                        RsiSignal: "NEUTRAL"
                    );
                }

                _logger.LogInformation("RSIAlgoStrategy: Received {Count} historical bars for {Symbol}", bars.Count, symbol.Symbol);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "RSIAlgoStrategy: Historical data request timed out for {Symbol}", symbol.Symbol);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Historical data request timed out: {ex.Message}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSIAlgoStrategy: Failed to get historical data for {Symbol}", symbol.Symbol);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Error retrieving historical data: {ex.Message}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            // Step 2: Extract close prices from bars
            var closePrices = bars.Select(b => (double)b.Close).ToArray();

            if (closePrices.Length < settings.Period + 1)
            {
                _logger.LogWarning("RSIAlgoStrategy: Insufficient data for {Symbol}. Need {Required} bars, got {Actual}", 
                    symbol.Symbol, settings.Period + 1, closePrices.Length);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Insufficient historical data: need {settings.Period + 1} bars, got {closePrices.Length}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            // Step 3: Calculate RSI series
            double[] rsiSeries;
            try
            {
                rsiSeries = RSICalculator.CalculateSeries(closePrices, settings.Period);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSIAlgoStrategy: Error calculating RSI for {Symbol}", symbol.Symbol);
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: $"Error calculating RSI: {ex.Message}",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: null,
                    RsiSignal: "NEUTRAL"
                );
            }

            var usableRsi = rsiSeries.Skip(settings.Period - 1).ToArray();
            if (usableRsi.Length < 2)
            {
                return new AlgoResult(
                    Symbol: symbol.Symbol,
                    Action: AlgoAction.Hold,
                    Price: symbol.LastPrice,
                    Reason: "Not enough RSI samples for crossover detection",
                    Timestamp: DateTime.UtcNow,
                    RsiValue: usableRsi.LastOrDefault(),
                    RsiSignal: "NEUTRAL"
                );
            }

            double prevRsi = usableRsi[^2];
            double currentRsi = usableRsi[^1];
            double ema = MovingAverage.CalculateEma(closePrices, TrendEmaPeriod);
            bool uptrend = closePrices[^1] >= ema;
            bool downtrend = !uptrend;

            var (action, signal, reason) = EvaluateSignal(symbol, settings, prevRsi, currentRsi, uptrend, downtrend, ema);

            _logger.LogInformation("RSIAlgoStrategy: {Action} signal for {Symbol} - RSI={RSI:F2}, Signal={Signal}", 
                action, symbol.Symbol, currentRsi, signal);

            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: action,
                Price: symbol.LastPrice,
                Reason: reason,
                Timestamp: DateTime.UtcNow,
                RsiValue: currentRsi,
                RsiSignal: signal
            );
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("RSIAlgoStrategy: Execution cancelled for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: "Algorithm execution was cancelled",
                Timestamp: DateTime.UtcNow,
                RsiValue: null,
                RsiSignal: "NEUTRAL"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSIAlgoStrategy: Unexpected error executing algorithm for {Symbol}", symbol.Symbol);
            return new AlgoResult(
                Symbol: symbol.Symbol,
                Action: AlgoAction.Hold,
                Price: symbol.LastPrice,
                Reason: $"Unexpected error: {ex.Message}",
                Timestamp: DateTime.UtcNow,
                RsiValue: null,
                RsiSignal: "NEUTRAL"
            );
        }
    }

    private (AlgoAction Action, string Signal, string Reason) EvaluateSignal(
        ScannerRowViewModel symbol,
        RsiSettings settings,
        double prevRsi,
        double currentRsi,
        bool uptrend,
        bool downtrend,
        double ema)
    {
        string priceInfo = $"Price ${symbol.LastPrice:F2} vs EMA{TrendEmaPeriod} {ema:F2}";

        if (prevRsi <= settings.Oversold && currentRsi > settings.Oversold)
        {
            return (AlgoAction.Buy,
                "STRONG BUY",
                $"RSI rebounded above oversold ({settings.Oversold}) -> STRONG BUY. {priceInfo}");
        }

        if (prevRsi < 50 && currentRsi >= 50 && uptrend)
        {
            return (AlgoAction.Buy,
                "BUY",
                $"RSI crossed above 50 with price above EMA -> BUY. {priceInfo}");
        }

        if (prevRsi >= settings.Overbought && currentRsi < settings.Overbought)
        {
            return (AlgoAction.Sell,
                "STRONG SELL",
                $"RSI rejected overbought ({settings.Overbought}) -> STRONG SELL. {priceInfo}");
        }

        if (prevRsi > 50 && currentRsi <= 50 && downtrend)
        {
            return (AlgoAction.Sell,
                "SELL",
                $"RSI crossed below 50 with price below EMA -> SELL. {priceInfo}");
        }

        var signal = uptrend ? "NEUTRAL (UPTREND)" : "NEUTRAL";
        return (AlgoAction.Hold,
            signal,
            $"RSI {currentRsi:F1} is neutral between levels. {priceInfo}");
    }
}

