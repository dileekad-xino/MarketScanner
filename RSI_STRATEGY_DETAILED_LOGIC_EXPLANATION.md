# RSI Strategy Algorithm - Detailed Logic Explanation

**MarketScanner Application**  
**Version:** 2.0 (Enhanced with 50→60/70 Swing Exit Logic)  
**Date:** 2024-11-26

---

## 📖 Table of Contents

1. [Overview](#overview)
2. [Complete Execution Flow](#complete-execution-flow)
3. [RSI Calculation Deep Dive](#rsi-calculation-deep-dive)
4. [Signal Evaluation Logic](#signal-evaluation-logic)
5. [State Management System](#state-management-system)
6. [Decision Tree & Priority System](#decision-tree--priority-system)
7. [Real-World Examples](#real-world-examples)
8. [Edge Cases & Special Scenarios](#edge-cases--special-scenarios)

---

## 🎯 Overview

The RSI Strategy is a **momentum-based trading algorithm** that analyzes price movements using the Relative Strength Index (RSI) indicator. It combines:

- **RSI Calculation**: Wilder's smoothing method (industry standard)
- **Trend Filter**: 20-period EMA to confirm direction
- **Stateful Tracking**: Remembers active positions for intelligent exits
- **Multi-Signal System**: 8 different signal types for comprehensive coverage

---

## 🔄 Complete Execution Flow

### Phase 1: Initialization & Data Fetching

```
┌─────────────────────────────────────────────────────────┐
│ ExecuteAsync(symbol, cancellationToken)                │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 1: Load Configuration                             │
│   - Get RsiSettings from RsiSettingsService            │
│   - Defaults: Period=14, Oversold=35, Overbought=65    │
│   - TakeProfitLevel=60, HistoricalDays=2, BarSize="1 min"│
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 2: Fetch Historical Bars from IBKR                │
│   - Call: GetHistoricalBarsForRSIAsync()               │
│   - Parameters: symbol, days, barSize                  │
│   - Returns: IReadOnlyList<Bar> with OHLCV data         │
│                                                         │
│   ERROR HANDLING:                                       │
│   ├─ No bars → Return HOLD with "No historical data"   │
│   ├─ Timeout → Return HOLD with timeout message       │
│   └─ Exception → Return HOLD with error message       │
└─────────────────────────────────────────────────────────┘
```

**Example Data Flow:**
```
Input: symbol = "AAPL", HistoricalDays = 2, BarSize = "1 min"
Output: ~480 bars (2 days × 6.5 hours × 60 minutes)
```

---

### Phase 2: Data Processing & Calculation

```
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 3: Extract Close Prices                           │
│   - Convert Bar.Close to double[]                      │
│   - Validate: Need at least (Period + 1) bars          │
│   - For Period=14: Need minimum 15 bars                 │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 4: Calculate RSI Series                           │
│   - Call: RSICalculator.CalculateSeries()              │
│   - Returns: double[] with RSI value for each bar      │
│   - Length: closePrices.Length - 1                     │
│                                                         │
│   PROCESS:                                              │
│   1. Calculate price changes (differences)              │
│   2. Separate gains and losses                         │
│   3. Apply Wilder's smoothing                          │
│   4. Calculate RS = avgGain / avgLoss                  │
│   5. Calculate RSI = 100 - (100 / (1 + RS))            │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 5: Prepare Usable RSI Values                       │
│   - Skip first (Period - 1) values (warm-up period)    │
│   - Extract last 2 values: prevRsi and currentRsi      │
│   - Validate: Need at least 2 values for crossover      │
│                                                         │
│   Example:                                              │
│   - RSI series length: 466                              │
│   - Skip first 13 (Period - 1)                         │
│   - Usable RSI: 453 values                              │
│   - prevRsi = usableRsi[451] (second to last)          │
│   - currentRsi = usableRsi[452] (last)                 │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 6: Calculate Trend (EMA20)                         │
│   - Call: MovingAverage.CalculateEma()                 │
│   - Period: 20 (hardcoded constant)                     │
│   - Compare: closePrices[^1] vs EMA                    │
│   - Result: uptrend = price >= EMA                     │
│             downtrend = price < EMA                    │
└─────────────────────────────────────────────────────────┘
```

**Visual Example:**
```
Close Prices: [100.0, 101.2, 99.8, 102.5, 103.1, ...]
                │      │      │      │      │
RSI Series:     [  -   ,  -   ,  -   , 52.3, 55.1, ...]
                (warm-up)          ↑      ↑
                              prevRsi currentRsi

EMA20: 101.5
Current Price: 103.1
Uptrend: true (103.1 >= 101.5)
```

---

### Phase 3: Context Management & Signal Evaluation

```
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 7: Check for Active Trade Context                  │
│   - Generate context key: "symbol|timeframe"           │
│   - Example: "AAPL|1 min"                               │
│   - Lookup in ConcurrentDictionary                     │
│   - Result: activeContext (null or RsiTradeContext)   │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 8: Evaluate Signal Logic                          │
│   - Call: EvaluateSignal()                             │
│   - Parameters:                                        │
│     • symbol, settings                                 │
│     • prevRsi, currentRsi                             │
│     • uptrend, downtrend, ema                          │
│     • activeContext, currentBarIndex                   │
│   - Returns: (Action, Signal, Reason,                  │
│              ShouldCloseContext, ShouldOpenContext)    │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 9: Update State Management                        │
│                                                         │
│   IF shouldCloseContext AND activeContext exists:      │
│     ├─ Remove context from dictionary                  │
│     └─ Log: "Closed 50-cross BUY context..."          │
│                                                         │
│   IF shouldOpenContext AND action == Buy AND            │
│      signal contains "50":                             │
│     ├─ Create new RsiTradeContext                      │
│     ├─ Store in dictionary                             │
│     └─ Log: "Opened 50-cross BUY context..."          │
│                                                         │
│   IF context still active:                             │
│     ├─ Update peak RSI tracking                        │
│     └─ Update flags (ReachedTakeProfitLevel, etc.)    │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│ Step 10: Return Result                                  │
│   - Create AlgoResult record                           │
│   - Include: Symbol, Action, Price, Reason,            │
│             Timestamp, RsiValue, RsiSignal             │
│   - Log final signal generation                        │
└─────────────────────────────────────────────────────────┘
```

---

## 🧮 RSI Calculation Deep Dive

### Wilder's Smoothing Method - Step by Step

#### **Step 1: Calculate Price Changes**

```csharp
// Input: closePrices = [100.0, 101.2, 99.8, 102.5, 103.1, ...]
// Output: changes = [1.2, -1.4, 2.7, 0.6, ...]

var changes = new double[closePrices.Length - 1];
for (int i = 0; i < changes.Length; i++)
{
    changes[i] = closePrices[i + 1] - closePrices[i];
}
```

**Example:**
```
Close: [100.0, 101.2, 99.8, 102.5]
Change: [  +1.2,  -1.4,  +2.7]
```

#### **Step 2: Separate Gains and Losses**

```csharp
// Gains: Only positive changes (or 0)
// Losses: Absolute value of negative changes (or 0)

gains[i] = changes[i] > 0 ? changes[i] : 0;
losses[i] = changes[i] < 0 ? Math.Abs(changes[i]) : 0;
```

**Example:**
```
Change:  [1.2, -1.4, 2.7, 0.6]
Gain:    [1.2,  0.0, 2.7, 0.6]
Loss:    [0.0,  1.4, 0.0, 0.0]
```

#### **Step 3: Initial Average (Simple Average)**

```csharp
// For Period = 14, take first 14 values
double avgGain = gains.Take(period).Average();
double avgLoss = losses.Take(period).Average();
```

**Example (Period = 14):**
```
First 14 gains: [1.2, 0.0, 2.7, 0.6, ...] (14 values)
avgGain = Sum of 14 gains / 14

First 14 losses: [0.0, 1.4, 0.0, 0.0, ...] (14 values)
avgLoss = Sum of 14 losses / 14
```

#### **Step 4: Wilder's Smoothing (Exponential Moving Average)**

```csharp
// Formula: New Avg = [(Previous Avg × (Period - 1)) + Current Value] / Period
// This gives more weight to recent values while maintaining smoothness

for (int i = period; i < changes.Length; i++)
{
    avgGain = ((avgGain * (period - 1)) + gains[i]) / period;
    avgLoss = ((avgLoss * (period - 1)) + losses[i]) / period;
}
```

**Mathematical Explanation:**
```
Wilder's Smoothing Formula:
NewAvg = (OldAvg × (N-1) + CurrentValue) / N

Where N = Period (typically 14)

This is equivalent to:
NewAvg = OldAvg × (N-1)/N + CurrentValue × 1/N

Example with Period = 14:
- OldAvg = 2.0
- CurrentValue = 3.0
- NewAvg = (2.0 × 13 + 3.0) / 14
         = (26.0 + 3.0) / 14
         = 29.0 / 14
         = 2.071

The smoothing factor is 13/14 (≈0.929) for old, 1/14 (≈0.071) for new.
This means 92.9% weight on history, 7.1% on current value.
```

#### **Step 5: Calculate RSI**

```csharp
// RS (Relative Strength) = Average Gain / Average Loss
double rs = avgGain / avgLoss;

// RSI = 100 - (100 / (1 + RS))
double rsi = 100.0 - (100.0 / (1.0 + rs));
```

**RSI Interpretation:**
```
RSI Range: 0 to 100

RSI < 30:  Oversold (potential buy)
RSI 30-50: Bearish momentum
RSI 50:    Neutral (midline)
RSI 50-70: Bullish momentum
RSI > 70:  Overbought (potential sell)

Example Calculation:
avgGain = 2.0
avgLoss = 1.0
RS = 2.0 / 1.0 = 2.0
RSI = 100 - (100 / (1 + 2.0))
    = 100 - (100 / 3)
    = 100 - 33.33
    = 66.67

This indicates bullish momentum (above 50, below overbought).
```

**Edge Case: No Losses**
```csharp
if (avgLoss == 0)
{
    return 100.0; // Perfect upward trend
}
```

---

## 🎯 Signal Evaluation Logic

### Complete Decision Tree

```
EvaluateSignal()
│
├─ [IF activeContext exists]
│  │
│  ├─ [Priority 1: 70-Rejection Check]
│  │  IF activeContext.ReachedOverbought == true
│  │  AND prevRsi >= Overbought
│  │  AND currentRsi < Overbought
│  │  → RETURN: STRONG SELL (70-rejection)
│  │     Close context
│  │
│  └─ [Priority 2: 60-Rejection Check]
│     IF activeContext.ReachedTakeProfitLevel == true
│     AND prevRsi >= TakeProfitLevel
│     AND currentRsi < TakeProfitLevel
│     AND NOT (prevRsi >= Overbought AND currentRsi < Overbought)
│     → RETURN: SELL (60-rejection)
│        Close context
│
└─ [Traditional Signals] (if no context exit triggered)
   │
   ├─ [Signal 1: STRONG BUY - Rebound]
   │  IF prevRsi <= Oversold
   │  AND currentRsi > Oversold
   │  → RETURN: STRONG BUY (rebound)
   │     Close context if exists
   │
   ├─ [Signal 2: STRONG BUY - Absolute]
   │  IF currentRsi <= Oversold
   │  → RETURN: STRONG BUY (absolute)
   │     Close context if exists
   │
   ├─ [Signal 3: BUY - 50-Cross]
   │  IF prevRsi < 50
   │  AND currentRsi >= 50
   │  AND uptrend == true
   │  → RETURN: BUY (50-cross)
   │     Close old context if exists
   │     OPEN NEW CONTEXT
   │
   ├─ [Signal 4: STRONG SELL - Rejection]
   │  IF prevRsi >= Overbought
   │  AND currentRsi < Overbought
   │  → RETURN: STRONG SELL (rejection)
   │     Close context if exists
   │
   ├─ [Signal 5: STRONG SELL - Absolute]
   │  IF currentRsi >= Overbought
   │  → RETURN: STRONG SELL (absolute)
   │     Close context if exists
   │
   ├─ [Signal 6: SELL - 50-Cross Down]
   │  IF prevRsi > 50
   │  AND currentRsi <= 50
   │  AND downtrend == true
   │  → RETURN: SELL (50-cross down)
   │     Close context if exists
   │
   └─ [Default: HOLD]
      → RETURN: HOLD (neutral)
         No context changes
```

### Detailed Signal Logic Explanation

#### **1. Swing Exit Logic (NEW - Highest Priority)**

**Why Check First?**
- These exits are **position-specific** (only valid if we have an active 50-cross BUY)
- They represent **take-profit** opportunities
- Should be evaluated **before** traditional signals to avoid conflicts

**70-Rejection (Strong Rally Exit):**
```csharp
if (activeContext.ReachedOverbought && 
    prevRsi >= settings.Overbought && 
    currentRsi < settings.Overbought)
{
    // RSI went above 70, peaked, and now falling back
    // This is a strong rally that's reversing
    return STRONG SELL;
}
```

**Logic Flow:**
1. **Check Flag**: `ReachedOverbought` must be `true` (RSI went above 70 at some point)
2. **Check Crossover**: `prevRsi >= 70` AND `currentRsi < 70` (crossing down)
3. **Action**: Generate STRONG SELL signal
4. **Context**: Close the active context

**60-Rejection (Mild Rally Exit):**
```csharp
if (activeContext.ReachedTakeProfitLevel && 
    prevRsi >= settings.TakeProfitLevel && 
    currentRsi < settings.TakeProfitLevel &&
    !(prevRsi >= settings.Overbought && currentRsi < settings.Overbought))
{
    // RSI went above 60, peaked, and now falling back
    // But NOT crossing below 70 (that would be stronger signal)
    return SELL;
}
```

**Priority Check:**
- The condition `!(prevRsi >= Overbought && currentRsi < Overbought)` ensures
- If RSI is dropping from 72 → 58 in one bar, only 70-rejection triggers
- 60-rejection is ignored in this case (70 takes precedence)

#### **2. Traditional BUY Signals**

**STRONG BUY - Rebound (Momentum Reversal):**
```csharp
if (prevRsi <= Oversold && currentRsi > Oversold)
{
    // RSI was oversold, now bouncing back
    // This is a momentum reversal from oversold territory
    return STRONG BUY;
}
```

**Example:**
```
Bar 1: RSI = 28 (oversold)
Bar 2: RSI = 36 (rebounded above 35)
→ STRONG BUY (momentum reversal)
```

**STRONG BUY - Absolute (Deep Oversold):**
```csharp
if (currentRsi <= Oversold)
{
    // Currently deeply oversold
    // Buy opportunity regardless of previous value
    return STRONG BUY;
}
```

**Example:**
```
Current RSI = 25 (below 35 threshold)
→ STRONG BUY (absolute level)
```

**BUY - 50-Cross (Momentum Confirmation):**
```csharp
if (prevRsi < 50 && currentRsi >= 50 && uptrend)
{
    // RSI crossing above 50 (neutral to bullish)
    // AND price is above EMA20 (trend confirmation)
    // This opens a new tracking context
    return BUY;
    // shouldOpenContext = true
}
```

**Why Trend Filter?**
- Prevents false signals in downtrends
- RSI can cross 50 in a downtrend (dead cat bounce)
- EMA20 confirms the trend is actually up

**Example:**
```
prevRsi = 48
currentRsi = 52
Price = $105.00
EMA20 = $103.50
uptrend = true (105.00 >= 103.50)

→ BUY (50-cross with uptrend)
→ Opens new trade context
```

#### **3. Traditional SELL Signals**

**STRONG SELL - Rejection (Mean Reversion):**
```csharp
if (prevRsi >= Overbought && currentRsi < Overbought)
{
    // RSI was overbought, now falling back
    // This works even without active context (pure mean-reversion)
    return STRONG SELL;
}
```

**Example:**
```
Bar 1: RSI = 72 (overbought)
Bar 2: RSI = 65 (rejected from overbought)
→ STRONG SELL (mean reversion)
```

**STRONG SELL - Absolute (Deep Overbought):**
```csharp
if (currentRsi >= Overbought)
{
    // Currently deeply overbought
    return STRONG SELL;
}
```

**SELL - 50-Cross Down (Momentum Reversal):**
```csharp
if (prevRsi > 50 && currentRsi <= 50 && downtrend)
{
    // RSI crossing below 50 (bullish to bearish)
    // AND price is below EMA20 (trend confirmation)
    return SELL;
}
```

**Example:**
```
prevRsi = 52
currentRsi = 48
Price = $98.00
EMA20 = $100.00
downtrend = true (98.00 < 100.00)

→ SELL (50-cross down with downtrend)
```

---

## 🔄 State Management System

### Context Lifecycle

#### **Opening a Context (50-Cross BUY)**

```csharp
if (shouldOpenContext && action == AlgoAction.Buy && signal.Contains("50"))
{
    var newContext = new RsiTradeContext(
        symbol.Symbol,           // "AAPL"
        settings.BarSize,        // "1 min"
        DateTime.UtcNow,         // Entry timestamp
        currentRsi,             // 52.3 (RSI at entry)
        usableRsi.Length - 1);   // Bar index: 452
    
    _activeTradeContexts[contextKey] = newContext;
}
```

**What Gets Stored:**
- Symbol and timeframe (for unique identification)
- Entry timestamp (when BUY occurred)
- Entry RSI value (for reference)
- Bar index (for debugging)
- Peak RSI (initialized to entry RSI)
- Flags (both false initially)

#### **Updating a Context (Every Bar While Active)**

```csharp
if (_activeTradeContexts.TryGetValue(contextKey, out var updatedContext))
{
    updatedContext.UpdatePeak(currentRsi, settings.TakeProfitLevel, settings.Overbought);
}
```

**UpdatePeak() Logic:**
```csharp
public void UpdatePeak(double currentRsi, double takeProfitLevel, double overboughtLevel)
{
    // Track highest RSI reached
    if (currentRsi > PeakRsi)
    {
        PeakRsi = currentRsi;
    }
    
    // Set flags when thresholds are crossed
    if (currentRsi >= takeProfitLevel)  // e.g., 60
    {
        ReachedTakeProfitLevel = true;  // Once true, stays true
    }
    
    if (currentRsi >= overboughtLevel)  // e.g., 70
    {
        ReachedOverbought = true;       // Once true, stays true
    }
}
```

**Example Progression:**
```
Bar 1 (Entry):  RSI = 52.3
  PeakRsi = 52.3
  ReachedTakeProfitLevel = false
  ReachedOverbought = false

Bar 2: RSI = 58.0
  PeakRsi = 58.0 (updated)
  ReachedTakeProfitLevel = false (58 < 60)

Bar 3: RSI = 63.0
  PeakRsi = 63.0 (updated)
  ReachedTakeProfitLevel = true (63 >= 60) ← Flag set!

Bar 4: RSI = 75.0
  PeakRsi = 75.0 (updated)
  ReachedTakeProfitLevel = true (stays true)
  ReachedOverbought = true (75 >= 70) ← Flag set!

Bar 5: RSI = 68.0
  PeakRsi = 75.0 (not updated, 68 < 75)
  ReachedTakeProfitLevel = true (stays true)
  ReachedOverbought = true (stays true)
  → 70-rejection signal triggers!
```

#### **Closing a Context (Exit Signals)**

```csharp
if (shouldCloseContext && activeContext != null)
{
    _activeTradeContexts.TryRemove(contextKey, out _);
    _logger.LogInformation("Closed 50-cross BUY context...");
}
```

**When Context Closes:**
1. **60-Rejection**: RSI falls below 60 after reaching it
2. **70-Rejection**: RSI falls below 70 after reaching it
3. **New 50-Cross BUY**: New entry closes old context
4. **STRONG BUY/SELL**: Strong signals close context
5. **50-Cross Down SELL**: Traditional exit closes context

---

## 🌳 Decision Tree & Priority System

### Complete Priority Hierarchy

```
Priority 1 (Highest): 70-Rejection Exit
  ├─ Only if: Active 50-cross context exists
  ├─ Condition: ReachedOverbought && prevRsi >= 70 && currentRsi < 70
  └─ Action: STRONG SELL, Close context

Priority 2: 60-Rejection Exit
  ├─ Only if: Active 50-cross context exists
  ├─ Condition: ReachedTakeProfitLevel && prevRsi >= 60 && currentRsi < 60
  ├─ Exception: NOT if also crossing below 70 (Priority 1 takes precedence)
  └─ Action: SELL, Close context

Priority 3: STRONG BUY - Rebound
  ├─ Condition: prevRsi <= Oversold && currentRsi > Oversold
  └─ Action: STRONG BUY, Close context if exists

Priority 4: STRONG BUY - Absolute
  ├─ Condition: currentRsi <= Oversold
  └─ Action: STRONG BUY, Close context if exists

Priority 5: BUY - 50-Cross
  ├─ Condition: prevRsi < 50 && currentRsi >= 50 && uptrend
  └─ Action: BUY, Open new context, Close old if exists

Priority 6: STRONG SELL - Rejection
  ├─ Condition: prevRsi >= Overbought && currentRsi < Overbought
  └─ Action: STRONG SELL, Close context if exists

Priority 7: STRONG SELL - Absolute
  ├─ Condition: currentRsi >= Overbought
  └─ Action: STRONG SELL, Close context if exists

Priority 8: SELL - 50-Cross Down
  ├─ Condition: prevRsi > 50 && currentRsi <= 50 && downtrend
  └─ Action: SELL, Close context if exists

Priority 9 (Lowest): HOLD
  └─ No conditions met → HOLD, No context changes
```

### Why This Order Matters

**Example Scenario:**
```
Active Context: Yes (50-cross BUY at RSI 52)
Current Bar: prevRsi = 72, currentRsi = 58

Evaluation:
1. Priority 1 (70-Rejection): 
   - ReachedOverbought = true ✓
   - prevRsi >= 70 (72 >= 70) ✓
   - currentRsi < 70 (58 < 70) ✓
   → TRIGGERS: STRONG SELL (70-rejection)
   → Context closed
   → Evaluation stops (no further checks)

If we checked Priority 2 first:
   - Would also trigger (60-rejection)
   - But 70-rejection is stronger signal
   - So Priority 1 must come first
```

---

## 📊 Real-World Examples

### Example 1: Complete Trade Cycle (Mild Rally)

```
Bar 1:  RSI = 48, Price = $100.00, EMA20 = $99.50
        → NEUTRAL (RSI below 50, but no cross yet)

Bar 2:  RSI = 52, Price = $101.50, EMA20 = $99.80
        → BUY (50-cross with uptrend)
        → Context opened: EntryRsi=52, PeakRsi=52

Bar 3:  RSI = 58, Price = $103.20, EMA20 = $100.20
        → HOLD (tracking)
        → Context updated: PeakRsi=58, ReachedTakeProfitLevel=false

Bar 4:  RSI = 63, Price = $105.40, EMA20 = $100.80
        → HOLD (tracking)
        → Context updated: PeakRsi=63, ReachedTakeProfitLevel=true ✓

Bar 5:  RSI = 59, Price = $104.10, EMA20 = $101.00
        → SELL (60-rejection, mild exit)
        → Context closed: PeakRsi was 63.0
        → Reason: "RSI reached 63.0 after prior 50-cross BUY and 
                   then fell back below 60 -> SELL (mild exit)"
```

**Trade Summary:**
- Entry: $101.50 (Bar 2)
- Exit: $104.10 (Bar 5)
- Peak RSI: 63.0
- Profit: $2.60 per share (2.56%)

---

### Example 2: Complete Trade Cycle (Strong Rally)

```
Bar 1:  RSI = 48, Price = $100.00, EMA20 = $99.50
        → NEUTRAL

Bar 2:  RSI = 52, Price = $101.50, EMA20 = $99.80
        → BUY (50-cross with uptrend)
        → Context opened: EntryRsi=52, PeakRsi=52

Bar 3:  RSI = 65, Price = $105.20, EMA20 = $100.20
        → HOLD (tracking)
        → Context updated: PeakRsi=65, ReachedTakeProfitLevel=true ✓

Bar 4:  RSI = 75, Price = $110.40, EMA20 = $101.00
        → HOLD (tracking)
        → Context updated: PeakRsi=75, ReachedOverbought=true ✓

Bar 5:  RSI = 68, Price = $108.10, EMA20 = $101.20
        → STRONG SELL (70-rejection, strong exit)
        → Context closed: PeakRsi was 75.0
        → Reason: "RSI reached 75.0 after prior 50-cross BUY and 
                   then fell back below 70 -> STRONG SELL (overbought rejection)"
```

**Trade Summary:**
- Entry: $101.50 (Bar 2)
- Exit: $108.10 (Bar 5)
- Peak RSI: 75.0
- Profit: $6.60 per share (6.50%)

---

### Example 3: Oversold Rebound (No Context)

```
Bar 1:  RSI = 38, Price = $95.00
        → NEUTRAL (oversold but no rebound)

Bar 2:  RSI = 28, Price = $92.00
        → NEUTRAL (still oversold, waiting for rebound)

Bar 3:  RSI = 36, Price = $94.50
        → STRONG BUY (rebound above 35)
        → No context opened (not a 50-cross)
        → Reason: "RSI rebounded above oversold (35) -> STRONG BUY"
```

**Note:** Oversold rebounds don't open tracking contexts because they're mean-reversion trades, not momentum trades.

---

### Example 4: Priority Conflict Resolution

```
Active Context: Yes (50-cross BUY at RSI 52)
Bar: prevRsi = 72, currentRsi = 58

Evaluation:
1. Check Priority 1 (70-Rejection):
   - ReachedOverbought = true ✓
   - prevRsi >= 70 (72 >= 70) ✓
   - currentRsi < 70 (58 < 70) ✓
   → TRIGGERS: STRONG SELL (70-rejection)
   → Context closed
   → Evaluation stops

Priority 2 (60-Rejection) is NOT checked because:
   - Priority 1 already triggered
   - Early return prevents further evaluation

Result: Only STRONG SELL (70-rejection) signal generated
```

---

## ⚠️ Edge Cases & Special Scenarios

### Edge Case 1: RSI Spike (50 → 75 in One Bar)

```
Bar 1:  RSI = 48, Price = $100.00
        → NEUTRAL

Bar 2:  RSI = 75, Price = $110.00 (spike!)
        → BUY (50-cross: 48 < 50, 75 >= 50, uptrend)
        → Context opened: EntryRsi=75, PeakRsi=75
        → ReachedOverbought=true (75 >= 70) ✓

Bar 3:  RSI = 68, Price = $108.00
        → STRONG SELL (70-rejection)
        → Context closed: PeakRsi was 75.0
```

**Handling:** Algorithm correctly:
1. Opens context on 50-cross (even though RSI is already 75)
2. Sets ReachedOverbought flag immediately
3. Triggers 70-rejection on next bar when RSI falls below 70

---

### Edge Case 2: Multiple Sequences (No Stale State)

```
Sequence 1:
Bar 1: RSI = 52 → BUY (context opened)
Bar 2: RSI = 59 → SELL (60-rejection, context closed)

Sequence 2 (same symbol, later):
Bar 3: RSI = 48 → NEUTRAL
Bar 4: RSI = 52 → BUY (new context opened, old one already closed)
Bar 5: RSI = 75 → HOLD (tracking)
Bar 6: RSI = 68 → STRONG SELL (70-rejection, context closed)
```

**Handling:** Each sequence is independent. Context is properly closed before new one opens.

---

### Edge Case 3: New BUY Before Exit

```
Bar 1: RSI = 52 → BUY (context opened: EntryRsi=52)
Bar 2: RSI = 58 → HOLD (tracking, PeakRsi=58)
Bar 3: RSI = 48 → NEUTRAL (dip, but no exit yet)
Bar 4: RSI = 52 → BUY (new 50-cross!)
        → Old context closed (shouldCloseContext=true)
        → New context opened (shouldOpenContext=true)
        → EntryRsi=52, PeakRsi=52
```

**Handling:** New 50-cross BUY closes old context and opens new one. Prevents stale state.

---

### Edge Case 4: Insufficient Data

```
Scenario: Only 10 bars available (need 15 for Period=14)

Flow:
1. Fetch bars: 10 bars received
2. Extract close prices: 10 values
3. Check: 10 < (14 + 1) = 15
4. Return: HOLD with "Insufficient historical data: need 15 bars, got 10"
```

**Handling:** Graceful degradation. Returns HOLD instead of crashing.

---

### Edge Case 5: No Losses (Perfect Uptrend)

```
Scenario: All price changes are positive (no losses)

RSI Calculation:
- avgLoss = 0
- Edge case check: if (avgLoss == 0) return 100.0
- RSI = 100 (perfect upward trend)

Signal: STRONG SELL (currentRsi >= Overbought: 100 >= 70)
```

**Handling:** Correctly returns RSI = 100 and generates appropriate signal.

---

## 🎓 Key Insights & Design Decisions

### Why Stateful Tracking?

**Problem Without State:**
- Can't remember if we entered on a 50-cross
- Can't track if RSI reached 60 or 70
- Can't generate intelligent exit signals

**Solution With State:**
- Remember entry conditions
- Track peak RSI and flags
- Generate context-aware exits

### Why Priority System?

**Problem Without Priority:**
- Multiple signals could trigger simultaneously
- Conflicting actions (BUY and SELL at same time)
- Unclear which signal to follow

**Solution With Priority:**
- Clear hierarchy (70 > 60 > traditional signals)
- Only one signal per evaluation
- Predictable behavior

### Why Trend Filter for 50-Cross?

**Problem Without Filter:**
- RSI can cross 50 in downtrends (false signals)
- Dead cat bounces trigger BUY signals
- Low success rate

**Solution With Filter:**
- EMA20 confirms trend direction
- Only BUY when price > EMA20 (uptrend)
- Only SELL when price < EMA20 (downtrend)
- Higher success rate

### Why Wilder's Smoothing?

**Industry Standard:**
- Most trading platforms use Wilder's method
- Consistent with professional tools
- Comparable results across systems

**Smoothing Benefits:**
- Reduces noise
- More stable RSI values
- Better signal quality

---

## 📝 Summary

The RSI Strategy Algorithm is a **sophisticated, stateful trading system** that:

1. **Calculates RSI** using industry-standard Wilder's smoothing
2. **Filters by trend** using EMA20 for confirmation
3. **Tracks positions** with per-symbol/timeframe contexts
4. **Generates 8 signal types** with clear priority hierarchy
5. **Handles edge cases** gracefully with comprehensive error handling
6. **Logs everything** for debugging and analysis

The enhanced **50→60/70 swing exit logic** adds professional-grade position management, allowing the algorithm to:
- Enter on momentum (50-cross with trend)
- Exit at optimal points (60 for mild rallies, 70 for strong rallies)
- Avoid premature exits while protecting profits

**Result:** A production-ready algorithmic trading system that combines technical analysis with intelligent position management.

---

*End of Detailed Logic Explanation*


