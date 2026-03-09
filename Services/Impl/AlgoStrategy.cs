using MarketScanner.Config;
using MarketScanner.Models;
using MarketScanner.Utilities;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;

namespace MarketScanner.Services.Impl;

/// <summary>
/// Central trade decision engine.
///
/// Indicators are pure/stateless.
/// AlgoStrategy is the ONLY place that:
/// - tracks runtime position state
/// - decides BUY / SELL / HOLD
/// - enforces entry and exit rules
/// </summary>
public sealed class AlgoStrategy : IAlgoStrategy
{
    private readonly IReadOnlyList<IAlgoStrategy> _strategies;
    private readonly ICciSettingsService _cciSettingsService;
    private readonly ICandlestickStorage _candlestickStorage;
    private readonly CandlestickConfig _candlestickConfig;
    private readonly ILogger<AlgoStrategy> _logger;
    private readonly ConcurrentDictionary<string, SymbolTradeState> _symbolStates = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Hybrid Momentum + ATR Strategy";
    public string Description => "Hybrid momentum entry + ATR profit-protection + bearish confirmation exit.";

    public AlgoStrategy(
        IEnumerable<IAlgoStrategy> strategies,
        ICciSettingsService cciSettingsService,
        ICandlestickStorage candlestickStorage,
        CandlestickConfig candlestickConfig,
        ILogger<AlgoStrategy> logger)
    {
        _strategies = strategies.ToList();
        _cciSettingsService = cciSettingsService;
        _candlestickStorage = candlestickStorage;
        _candlestickConfig = candlestickConfig;
        _logger = logger;
    }

    public async Task<AlgoResult> ExecuteAsync(
        ScannerRowViewModel symbol,
        bool hasOpenPosition = false,
        CancellationToken ct = default)
    {
        try
        {
            var cciSettingsTask = _cciSettingsService.GetAsync(symbol.Symbol, ct);
            var strategyTasks = _strategies
                .Select(strategy => Task.Run(() => strategy.ExecuteAsync(symbol, hasOpenPosition, ct), ct))
                .ToArray();

            await Task.WhenAll(strategyTasks).ConfigureAwait(false);
            var results = strategyTasks.Select(t => t.Result).ToArray();
            var settings = await cciSettingsTask.ConfigureAwait(false);

            return CombineResults(results, symbol, hasOpenPosition, settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AlgoStrategy failed for {Symbol}", symbol.Symbol);
            return Hold(symbol, $"Algo error: {ex.Message}");
        }
    }

    private AlgoResult CombineResults(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        bool hasOpenPosition,
        CciSettings settings)
    {
        if (results.Count == 0)
            return Hold(symbol, "No indicator results");

        var cci = results.FirstOrDefault(r => r.CciValue.HasValue || !string.IsNullOrWhiteSpace(r.CciSignal));
        var ema = results.FirstOrDefault(r => r.Ema20Value.HasValue || !string.IsNullOrWhiteSpace(r.Ema20Signal));
        var atr = results.FirstOrDefault(r => r.AtrValue.HasValue || (r.Reason?.Contains("ATR", StringComparison.OrdinalIgnoreCase) ?? false));

        var price = symbol.LastPrice;
        var previousClose = ResolvePreviousClose(symbol, ema);

        if (!hasOpenPosition)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            return EvaluateEntry(results, symbol, settings, cci, ema, atr, price, previousClose);
        }

        var state = _symbolStates.GetOrAdd(symbol.Symbol, _ => new SymbolTradeState
        {
            InPosition = true,
            EntryPrice = price,
            HighestPriceSinceEntry = price,
            IsTrailingArmed = false,
            TrailingStop = null
        });

        lock (state.Sync)
        {
            state.InPosition = true;
            if (!state.EntryPrice.HasValue || state.EntryPrice.Value <= 0)
            {
                state.EntryPrice = price;
            }

            if (!state.HighestPriceSinceEntry.HasValue || price > state.HighestPriceSinceEntry.Value)
            {
                state.HighestPriceSinceEntry = price;
            }

            return EvaluateExit(results, symbol, settings, cci, ema, atr, price, previousClose, state);
        }
    }

    private AlgoResult EvaluateEntry(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        CciSettings settings,
        AlgoResult? cci,
        AlgoResult? ema,
        AlgoResult? atr,
        double? close,
        double? previousClose)
    {
        if (!close.HasValue)
        {
            return BuildResult(symbol, AlgoAction.Hold, "HOLD (flat): close unavailable", results, cci, ema, atr, previousClose);
        }

        var cciNow = cci?.CciValue;
        var d1 = cci?.CciDelta;
        var acc = cci?.CciAcceleration;
        var emaNow = ema?.Ema20Value;
        var emaPrev = ema?.PreviousEma20Value;
        var atrValue = atr?.AtrValue;

        var entryThreshold = CciSettings.NormalizeEntryThreshold(settings.EntryThreshold);
        var entryMinDelta = CciSettings.NormalizeEntryMinDelta(settings.EntryMinDelta);
        var impulseMultiplier = CciSettings.NormalizeImpulseAtrMultiplier(settings.ImpulseAtrMultiplier);
        var requireRisingEma = settings.RequireRisingEma20;

        var hasCore = cciNow.HasValue && d1.HasValue && acc.HasValue && emaNow.HasValue && previousClose.HasValue;

        bool cciOk = hasCore && cciNow!.Value > entryThreshold;
        bool d1Ok = hasCore && d1!.Value >= entryMinDelta;
        bool accOk = hasCore && acc!.Value >= 0;
        bool closeAboveEma = hasCore && close.Value > emaNow!.Value;
        bool closeAbovePrev = hasCore && close.Value > previousClose!.Value;

        bool impulseOk = false;
        double? impulse = null;
        double? impulseThreshold = null;
        if (hasCore && atrValue.HasValue)
        {
            impulse = close.Value - previousClose!.Value;
            impulseThreshold = atrValue.Value * impulseMultiplier;
            impulseOk = impulse.Value >= impulseThreshold.Value;
        }

        bool emaRisingOk = !requireRisingEma || (emaNow.HasValue && emaPrev.HasValue && emaNow.Value > emaPrev.Value);

        bool canEnter = hasCore && atrValue.HasValue && impulse.HasValue && impulseThreshold.HasValue
            && cciOk && d1Ok && accOk && closeAboveEma && closeAbovePrev && impulseOk && emaRisingOk;

        if (canEnter)
        {
            _symbolStates[symbol.Symbol] = new SymbolTradeState
            {
                InPosition = true,
                EntryPrice = close,
                HighestPriceSinceEntry = close,
                IsTrailingArmed = false,
                TrailingStop = null
            };

            var summary =
                $"BUY: CCI {Fmt(cciNow)} > {entryThreshold:F0}, d1 {Fmt(d1)} >= {entryMinDelta:F0}, acc {Fmt(acc)} >= 0, " +
                $"close {Fmt(close)} > EMA20 {Fmt(emaNow)}, close > prevClose, impulse {Fmt(impulse)} >= ATR*{impulseMultiplier:F2} {Fmt(impulseThreshold)}, " +
                $"EMA20 {(emaRisingOk ? "rising" : "not rising")}";

            return BuildResult(symbol, AlgoAction.Buy, summary, results, cci, ema, atr, previousClose);
        }

        var holdSummary =
            "HOLD (flat): entry conditions not met: " +
            $"CCI>{entryThreshold:F0}={YesNo(cciOk)}, " +
            $"d1>={entryMinDelta:F0}={YesNo(d1Ok)}, " +
            $"acc>=0={YesNo(accOk)}, " +
            $"close>EMA20={YesNo(closeAboveEma)}, " +
            $"close>prevClose={YesNo(closeAbovePrev)}, " +
            $"impulse>=ATR*{impulseMultiplier:F2}={YesNo(impulseOk)}, " +
            $"EMA20 rising={YesNo(emaRisingOk)}";

        return BuildResult(symbol, AlgoAction.Hold, holdSummary, results, cci, ema, atr, previousClose);
    }

    private AlgoResult EvaluateExit(
        IReadOnlyList<AlgoResult> results,
        ScannerRowViewModel symbol,
        CciSettings settings,
        AlgoResult? cci,
        AlgoResult? ema,
        AlgoResult? atr,
        double? close,
        double? previousClose,
        SymbolTradeState state)
    {
        if (!close.HasValue || !state.EntryPrice.HasValue || !state.HighestPriceSinceEntry.HasValue)
        {
            return BuildResult(symbol, AlgoAction.Hold, "HOLD (in position): waiting for price state", results, cci, ema, atr, previousClose);
        }

        var entryPrice = state.EntryPrice.Value;
        var high = state.HighestPriceSinceEntry.Value;
        var atrValue = atr?.AtrValue;
        var trailingArmAtrMultiplier = CciSettings.NormalizeTrailingArmAtrMultiplier(settings.TrailingArmAtrMultiplier);
        double? armPrice = null;
        if (atrValue.HasValue)
        {
            armPrice = entryPrice + (atrValue.Value * trailingArmAtrMultiplier);
            if (!state.IsTrailingArmed && close.Value >= armPrice.Value)
            {
                state.IsTrailingArmed = true;
            }
        }

        double? trail = null;
        var trailingAtrMultiplier = CciSettings.NormalizeTrailingAtrMultiplier(settings.TrailingAtrMultiplier);
        if (state.IsTrailingArmed && atrValue.HasValue)
        {
            trail = high - (atrValue.Value * trailingAtrMultiplier);
            state.TrailingStop = trail;
        }

        if (state.IsTrailingArmed && trail.HasValue && close.Value <= trail.Value)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            var summary = $"SELL: profit-protection trailing stop hit | entry {Fmt(entryPrice)} | high {Fmt(high)} | ATR {Fmt(atrValue)} | trail {Fmt(trail)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Sell, summary, results, cci, ema, atr, previousClose);
        }

        var d1 = cci?.CciDelta;
        var acc = cci?.CciAcceleration;
        var emaNow = ema?.Ema20Value;

        bool bearish = d1.HasValue && acc.HasValue && emaNow.HasValue && previousClose.HasValue
                       && d1.Value < 0
                       && acc.Value < 0
                       && close.Value < emaNow.Value
                       && close.Value < previousClose.Value;

        if (bearish)
        {
            _symbolStates.TryRemove(symbol.Symbol, out _);
            var summary =
                $"SELL: momentum reversal confirmed | d1 {Fmt(d1)} < 0, acc {Fmt(acc)} < 0, close {Fmt(close)} < EMA20 {Fmt(emaNow)}, close < prevClose";
            return BuildResult(symbol, AlgoAction.Sell, summary, results, cci, ema, atr, previousClose);
        }

        if (!state.IsTrailingArmed)
        {
            if (armPrice.HasValue)
            {
                var summary = $"HOLD (in position): trailing not armed yet | entry {Fmt(entryPrice)} | ATR {Fmt(atrValue)} | armMult {Fmt(trailingArmAtrMultiplier)} | arm at {Fmt(armPrice)} | high {Fmt(high)} | close {Fmt(close)}";
                return BuildResult(symbol, AlgoAction.Hold, summary, results, cci, ema, atr, previousClose);
            }

            var summaryNoAtr = $"HOLD (in position): trailing not armed yet | ATR unavailable (arming requires ATR) | entry {Fmt(entryPrice)} | high {Fmt(high)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Hold, summaryNoAtr, results, cci, ema, atr, previousClose);
        }

        if (!trail.HasValue)
        {
            var summary = $"HOLD (in position): trailing armed but ATR unavailable | entry {Fmt(entryPrice)} | high {Fmt(high)} | close {Fmt(close)}";
            return BuildResult(symbol, AlgoAction.Hold, summary, results, cci, ema, atr, previousClose);
        }

        var holdArmedSummary =
            $"HOLD (in position): trailing armed | entry {Fmt(entryPrice)} | high {Fmt(high)} | ATR {Fmt(atrValue)} | trail {Fmt(trail)} | close {Fmt(close)} above trail";
        return BuildResult(symbol, AlgoAction.Hold, holdArmedSummary, results, cci, ema, atr, previousClose);
    }

    private AlgoResult BuildResult(
        ScannerRowViewModel symbol,
        AlgoAction action,
        string summary,
        IReadOnlyList<AlgoResult> allResults,
        AlgoResult? cci,
        AlgoResult? ema,
        AlgoResult? atr,
        double? previousClose)
    {
        var indicatorReasons = string.Join(" | ", allResults.Select(r => $"[{r.Action}] {r.Reason}"));

        return new AlgoResult(
            Symbol: symbol.Symbol,
            Action: action,
            Price: symbol.LastPrice,
            Reason: $"{summary} | {indicatorReasons}",
            Timestamp: DateTime.UtcNow,
            CciValue: cci?.CciValue,
            CciSignal: cci?.CciSignal,
            PreviousCciValue: cci?.PreviousCciValue,
            PreviousCciValue2: cci?.PreviousCciValue2,
            CciDelta: cci?.CciDelta,
            CciDeltaPrevious: cci?.CciDeltaPrevious,
            CciAcceleration: cci?.CciAcceleration,
            Ema20Value: ema?.Ema20Value,
            PreviousEma20Value: ema?.PreviousEma20Value,
            Ema20Signal: ema?.Ema20Signal,
            AtrValue: atr?.AtrValue,
            PreviousClose: previousClose,
            Macd: allResults.FirstOrDefault(r => r.Macd != null)?.Macd,
            MacdSignal: allResults.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.MacdSignal))?.MacdSignal
        );
    }

    private double? ResolvePreviousClose(ScannerRowViewModel symbol, AlgoResult? ema)
    {
        if (ema?.PreviousClose.HasValue == true)
            return ema.PreviousClose.Value;

        var interval = TimeframeMap.ToIntervalKey(_candlestickConfig.IntervalSeconds);
        var candles = _candlestickStorage
            .GetCandlesticks(symbol.Symbol, interval, 2)
            .OrderBy(c => c.Timestamp)
            .ToList();

        if (candles.Count >= 2)
            return (double)candles[^2].Close;

        return symbol.PrevClose > 0 ? symbol.PrevClose : null;
    }

    private static AlgoResult Hold(ScannerRowViewModel symbol, string reason) =>
        new(
            Symbol: symbol.Symbol,
            Action: AlgoAction.Hold,
            Price: symbol.LastPrice,
            Reason: reason,
            Timestamp: DateTime.UtcNow
        );

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Fmt(double? value) => value.HasValue
        ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
        : "n/a";

    private sealed class SymbolTradeState
    {
        public object Sync { get; } = new();
        public bool InPosition { get; set; }
        public double? EntryPrice { get; set; }
        public double? HighestPriceSinceEntry { get; set; }
        public bool IsTrailingArmed { get; set; }
        public double? TrailingStop { get; set; }
    }
}
