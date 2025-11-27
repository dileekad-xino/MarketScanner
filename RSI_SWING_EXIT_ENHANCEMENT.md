# RSI Strategy Enhancement: 50→60/70 Swing Exit Logic

## Overview

This document describes the enhanced RSI strategy implementation with stateful 50→60/70 swing exit logic. The enhancement adds sophisticated position tracking and take-profit mechanisms while preserving all existing signal types.

## 🎯 Feature Summary

### What's New

1. **Stateful Position Tracking**: The strategy now tracks active 50-cross BUY positions per symbol/timeframe
2. **Swing Exit at 60 (Mild Rally)**: Automatic exit when RSI peaks between 60-70 and rejects below 60
3. **Swing Exit at 70 (Strong Rally)**: Automatic exit when RSI reaches overbought (70+) and rejects below 70
4. **Configurable Take-Profit Level**: New `TakeProfitLevel` setting (default: 60.0) for mild rally exits
5. **Priority-Based Exit Logic**: 70-rejection takes precedence over 60-rejection on the same bar

### Backward Compatibility ✅

All existing RSI signals remain fully functional:
- ✅ STRONG BUY on oversold rebound (30-level)
- ✅ STRONG BUY on absolute oversold (RSI ≤ 30)
- ✅ BUY on 50-cross with uptrend (now opens tracking context)
- ✅ STRONG SELL on overbought rejection (70-level)
- ✅ STRONG SELL on absolute overbought (RSI ≥ 70)
- ✅ SELL on 50-cross down with downtrend

---

## 📊 Signal Logic

### Entry Signal (Opens Trade Context)

**BUY: RSI 50-Cross with Uptrend**
- **Condition**: `prevRsi < 50 && currentRsi >= 50 && uptrend`
- **Action**: Generate BUY signal + Open trade context for tracking
- **Context Stored**: Symbol, timeframe, entry RSI, entry timestamp, bar index
- **Trend Filter**: Price must be above EMA20

### Exit Signals (Closes Trade Context)

#### Exit A: Mild Rally (60-Rejection)

**SELL: Take-Profit at 60-Level**
- **Condition**: 
  - Active 50-cross BUY context exists
  - RSI reached TakeProfitLevel (60) after entry
  - RSI crosses back below 60: `prevRsi >= 60 && currentRsi < 60`
- **Action**: Generate SELL signal + Close trade context
- **Reason**: `"RSI reached {peakRsi} after prior 50-cross BUY and then fell back below 60 -> SELL (mild exit)"`

#### Exit B: Strong Rally (70-Rejection)

**STRONG SELL: Overbought Rejection**
- **Condition**:
  - Active 50-cross BUY context exists
  - RSI reached Overbought (70) after entry
  - RSI crosses back below 70: `prevRsi >= 70 && currentRsi < 70`
- **Action**: Generate STRONG SELL signal + Close trade context
- **Reason**: `"RSI reached {peakRsi} after prior 50-cross BUY and then fell back below 70 -> STRONG SELL (overbought rejection)"`
- **Priority**: Takes precedence over 60-rejection if both trigger on same bar

---

## 🏗️ Architecture

### New Components

#### 1. RsiTradeContext (`Models/RsiTradeContext.cs`)

```csharp
public sealed class RsiTradeContext
{
    public string Symbol { get; init; }
    public string Timeframe { get; init; }
    public DateTime EntryTimestamp { get; init; }
    public double EntryRsi { get; init; }
    public int EntryBarIndex { get; init; }
    public double PeakRsi { get; set; }
    public bool ReachedTakeProfitLevel { get; set; }
    public bool ReachedOverbought { get; set; }
    
    public void UpdatePeak(double currentRsi, double takeProfitLevel, double overboughtLevel);
}
```

**Purpose**: Tracks active positions entered via 50-cross BUY signals

**Tracking**:
- Entry details (symbol, timeframe, RSI, timestamp)
- Peak RSI achieved since entry
- Flags for reaching 60-level and 70-level

#### 2. Enhanced RsiSettings

```csharp
public class RsiSettings
{
    public int Period { get; set; } = 14;
    public double Oversold { get; set; } = 35.0;
    public double Overbought { get; set; } = 65.0;
    public double TakeProfitLevel { get; set; } = 60.0;  // NEW
    public int HistoricalDays { get; set; } = 2;
    public string BarSize { get; set; } = "1 min";
}
```

**New Setting**: `TakeProfitLevel` (default: 60.0)
- Used for mild rally exits
- Must be between 50 and Overbought
- Configurable via UI settings dialog

#### 3. State Management in RSIAlgoStrategy

```csharp
private static readonly ConcurrentDictionary<string, RsiTradeContext> _activeTradeContexts = new();
```

**Key Format**: `"{symbol}|{timeframe}"` (e.g., "AAPL|1 min")

**Thread-Safe**: Uses `ConcurrentDictionary` for multi-symbol concurrent execution

**Lifecycle**:
- Opened: When 50-cross BUY signal is generated
- Updated: On every subsequent bar to track peak RSI
- Closed: When exit signal triggers or new opposite signal occurs

---

## 🔄 Execution Flow

### Step 1: Calculate RSI and Trends
```
1. Fetch historical bars from IBKR
2. Calculate RSI series using Wilder's smoothing
3. Calculate EMA20 for trend detection
4. Determine uptrend/downtrend (price vs EMA)
```

### Step 2: Check for Exit Conditions (if context exists)
```
IF active trade context exists:
    Priority 1: Check 70-rejection
        IF ReachedOverbought AND prevRsi >= 70 AND currentRsi < 70:
            -> STRONG SELL + close context
    
    Priority 2: Check 60-rejection (if 70 didn't trigger)
        IF ReachedTakeProfitLevel AND prevRsi >= 60 AND currentRsi < 60:
            -> SELL + close context
```

### Step 3: Evaluate Traditional Signals
```
Traditional signals still apply when no context-specific exit triggers:
- STRONG BUY: Oversold rebound / absolute oversold
- BUY: 50-cross with uptrend (opens NEW context)
- STRONG SELL: Overbought rejection / absolute overbought
- SELL: 50-cross down with downtrend
```

### Step 4: State Management
```
IF signal triggers context closure:
    Remove context from dictionary
    Log closure with entry/peak details

IF signal opens new context (50-cross BUY):
    Create new RsiTradeContext
    Store in dictionary with symbol|timeframe key
    Log opening with entry details

IF context still active:
    Update peak RSI tracking
    Update reached-level flags
```

---

## 🧪 Testing

### Unit Test Coverage

**Test File**: `Tests/RSIAlgoStrategyTests.cs`

#### Test Cases

1. **Test_50_To_60_Rejection_GeneratesSellSignal**
   - RSI: 45 → 49 → 51 (BUY) → 55 → 61 → 63 → 59 (SELL)
   - Validates mild rally exit at 60-rejection

2. **Test_50_To_70_Rejection_GeneratesStrongSellSignal**
   - RSI: 48 → 50.5 (BUY) → 65 → 72 → 78 → 71 → 69 (SELL)
   - Validates strong rally exit at 70-rejection

3. **Test_NoExitTriggered_WhenRsiNeverReaches60**
   - RSI: 45 → 48 → 52 → 55 → 57 → 56 → 54
   - Validates no premature exit when RSI stays below 60

4. **Test_MultipleSequences_NoStaleState**
   - Tests multiple trading sequences on same symbol
   - Validates context cleanup between trades

5. **Test_ExistingOversoldReboundLogic_StillWorks**
   - RSI: 35 → 28 → 25 → 32
   - Validates backward compatibility with oversold signals

6. **Test_RsiSpike_From_Below50_To_Above70_InOneBar**
   - RSI: 45 → 48 → 75 → 72
   - Validates edge case handling for rapid spikes

7. **Test_70Rejection_TakesPriority_Over_60Rejection**
   - RSI: 48 → 52 → 65 → 72 → 58 (single bar drop)
   - Validates priority logic when both exits trigger

8. **Test_RsiSettings_IncludesTakeProfitLevel**
   - Validates new setting exists with default value

9. **Test_RsiTradeContext_UpdatesPeakCorrectly**
   - Validates peak tracking and flag updates

### Running Tests

```bash
# Using dotnet test (if test project is set up)
dotnet test --filter "FullyQualifiedName~RSIAlgoStrategyTests"

# Using Visual Studio
# Open Test Explorer and run "RSIAlgoStrategyTests"
```

---

## 🎨 UI Updates

### Settings Dialog Enhancement

**File**: `Views/Dialogs/RsiSettingsPopup.xaml`

**New Field**: Take Profit Level
- **Label**: "Take Profit Level"
- **Input**: Numeric entry (0.## format)
- **Default**: 60.0
- **Validation**: Must be between 50 and Overbought level
- **Position**: Between "Overbought" and "Historical Days"

**Updated Layout**:
```
1. Length (Period)
2. Oversold
3. Overbought
4. Take Profit Level  ← NEW
5. Historical Days
6. Bar Size
```

### Validation Rules

```csharp
if (!double.TryParse(TakeProfitEntry.Text, out var takeProfit) 
    || takeProfit <= 50 
    || takeProfit >= overbought)
{
    error = $"Take Profit Level must be between 50 and Overbought ({overbought}).";
    return false;
}
```

---

## 📝 Logging

### Context Lifecycle Logs

**Opening Context**:
```
RSIAlgoStrategy: Opened 50-cross BUY context for AAPL (timeframe=1 min, rsi=52.3, bar=45)
```

**Closing Context**:
```
RSIAlgoStrategy: Closed 50-cross BUY context for AAPL (timeframe=1 min, entryRsi=52.3, peakRsi=68.7)
```

### Signal Generation Logs

**60-Rejection Exit**:
```
RSIAlgoStrategy: Sell signal for AAPL - RSI=59.2, Signal=SELL, ActiveContext=True
Reason: "RSI reached 68.7 after prior 50-cross BUY and then fell back below 60 -> SELL (mild exit). Price $175.45 vs EMA20 173.20"
```

**70-Rejection Exit**:
```
RSIAlgoStrategy: Sell signal for AAPL - RSI=69.3, Signal=STRONG SELL, ActiveContext=True
Reason: "RSI reached 78.2 after prior 50-cross BUY and then fell back below 70 -> STRONG SELL (overbought rejection). Price $180.20 vs EMA20 173.20"
```

---

## 🔧 Configuration Examples

### Conservative Settings (Early Exits)
```json
{
  "Period": 14,
  "Oversold": 30.0,
  "Overbought": 70.0,
  "TakeProfitLevel": 55.0,  // Exit sooner
  "HistoricalDays": 2,
  "BarSize": "1 min"
}
```

### Aggressive Settings (Ride Trends)
```json
{
  "Period": 14,
  "Oversold": 30.0,
  "Overbought": 75.0,  // Higher threshold
  "TakeProfitLevel": 65.0,  // Hold longer
  "HistoricalDays": 2,
  "BarSize": "1 min"
}
```

---

## 🎯 Acceptance Criteria ✅

### ✅ Entry Signals
- [x] BUY signal generated when RSI crosses above 50 in uptrend
- [x] Trade context opened and logged
- [x] Context key includes symbol and timeframe

### ✅ Exit Signals
- [x] SELL generated when RSI goes above 60 then crosses back below
- [x] STRONG SELL generated when RSI goes above 70 then crosses back below
- [x] 70-rejection takes priority over 60-rejection on same bar
- [x] Trade context closed and logged on exit

### ✅ Backward Compatibility
- [x] Oversold rebound signals (30-level) still work
- [x] Overbought rejection signals (70-level) still work without context
- [x] 50-cross down signals still work

### ✅ Edge Cases
- [x] RSI spike from <50 to >70 in one bar handled correctly
- [x] Multiple sequences on same symbol don't leave stale state
- [x] New 50-cross in opposite direction closes old context

### ✅ Configuration
- [x] TakeProfitLevel setting added to RsiSettings
- [x] UI includes Take Profit Level field
- [x] Validation ensures level is between 50 and Overbought

### ✅ Testing
- [x] Unit tests cover all new signal patterns
- [x] Tests validate state management
- [x] Tests confirm backward compatibility

---

## 🚀 Usage

### In MarketScanner Application

1. **Configure Settings**:
   - Navigate to RSI Settings dialog
   - Set TakeProfitLevel (default 60.0)
   - Adjust Overbought/Oversold as needed

2. **Run Algo Strategy**:
   - Select symbols in scanner
   - Click "Run Algo" to execute RSI strategy
   - Monitor logs for signal generation

3. **Monitor Results**:
   - Watch for BUY signals on 50-cross
   - Track SELL signals at 60/70 rejections
   - Review Reason strings for signal details

### Example Trading Scenario

```
Bar 1:  RSI=48  Price=100.00  -> NEUTRAL
Bar 2:  RSI=52  Price=101.50  -> BUY (50-cross, context opened)
Bar 3:  RSI=58  Price=103.20  -> HOLD (tracking, no exit yet)
Bar 4:  RSI=63  Price=105.40  -> HOLD (above 60, tracking peak)
Bar 5:  RSI=59  Price=104.10  -> SELL (60-rejection, mild exit)
                                  Context closed, peak was 63.0
```

---

## 📚 References

### Related Files

**Models**:
- `Models/RsiSettings.cs` - Configuration model
- `Models/RsiTradeContext.cs` - Position tracking model
- `Models/AlgoResult.cs` - Signal result model

**Strategy**:
- `Services/Impl/RSIAlgoStrategy.cs` - Core strategy logic
- `Services/IAlgoStrategy.cs` - Strategy interface

**Utilities**:
- `Utilities/RSICalculator.cs` - RSI calculation (Wilder's method)
- `Utilities/MovingAverage.cs` - EMA calculation

**UI**:
- `Views/Dialogs/RsiSettingsPopup.xaml` - Settings UI
- `Views/Dialogs/RsiSettingsPopup.xaml.cs` - Settings code-behind

**Tests**:
- `Tests/RSIAlgoStrategyTests.cs` - Unit tests

### External Documentation

- [Wilder's RSI Calculation](https://en.wikipedia.org/wiki/Relative_strength_index)
- [EMA Trend Filters](https://www.investopedia.com/terms/e/ema.asp)
- [Swing Trading Strategies](https://www.investopedia.com/terms/s/swingtrading.asp)

---

## 📋 Changelog

### Version 2.0 - Swing Exit Enhancement

**Added**:
- Stateful position tracking via `RsiTradeContext`
- Swing exit at 60-level (mild rally)
- Swing exit at 70-level (strong rally)
- `TakeProfitLevel` configuration setting
- Priority-based exit logic (70 > 60)
- Context lifecycle logging
- UI field for Take Profit Level
- Comprehensive unit test suite

**Changed**:
- `EvaluateSignal` now returns 5-tuple (includes context flags)
- `ExecuteAsync` includes state management logic
- Settings dialog layout (added new field)

**Preserved**:
- All existing signal types (STRONG BUY/SELL, BUY/SELL, HOLD)
- EMA20 trend filter for 50-cross entries
- Wilder's RSI calculation method
- Async IBKR data fetching
- Cancellation token support

---

## 🤝 Contributing

When extending this strategy:

1. **Preserve Backward Compatibility**: Existing signals must continue working
2. **Update Tests**: Add test cases for new logic
3. **Log State Changes**: All context opens/closes should be logged
4. **Validate Settings**: Ensure new settings have proper validation
5. **Document Changes**: Update this file with new behavior

---

## ⚠️ Known Limitations

1. **Per-Symbol State**: Context is maintained per symbol+timeframe, not per actual position
2. **Static Dictionary**: Uses static dictionary for state (consider dependency injection for testability)
3. **No Position Sizing**: Strategy generates signals only, doesn't manage position sizes
4. **Single Active Context**: Only one context per symbol+timeframe (no scaling in/out)
5. **Mock Challenges**: Unit tests use mocks; integration tests with real IBKR data recommended

---

## 🎓 Best Practices

### For Traders

- **Start Conservative**: Use default settings (60.0 take-profit) initially
- **Monitor Peak RSI**: Review logs to see typical peak levels for your symbols
- **Adjust for Volatility**: Higher volatility may need wider take-profit levels
- **Combine with Other Indicators**: RSI works best with volume and price action confirmation

### For Developers

- **Thread Safety**: Always use thread-safe collections for state
- **Logging**: Log all state transitions for debugging
- **Testing**: Use synthetic data for deterministic test results
- **Error Handling**: Gracefully handle missing data and timeouts
- **Documentation**: Keep this file updated with changes

---

*Last Updated: 2024-11-26*
*Version: 2.0*
*Author: RSI Strategy Enhancement Team*

