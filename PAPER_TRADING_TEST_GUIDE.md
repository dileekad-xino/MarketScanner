# Paper Trading Test Guide for RSI Algorithm

## Overview

This guide explains how to test the RSI-based trading algorithm using IBKR's paper trading environment. **Always test algorithms in paper trading mode before using real money.**

## Prerequisites

1. **IBKR Paper Trading Account**
   - Sign up for a free paper trading account at [IBKR Paper Trading](https://www.interactivebrokers.com/en/index.php?f=16042)
   - Paper trading accounts have virtual money and simulate real market conditions

2. **IB Gateway or TWS Setup**
   - Download and install [IB Gateway](https://www.interactivebrokers.com/en/index.php?f=16457) or [Trader Workstation (TWS)](https://www.interactivebrokers.com/en/index.php?f=16042)
   - **Important**: Use the **Paper Trading** version, not the live version

3. **Market Data Subscriptions**
   - Paper trading accounts include free delayed market data
   - No additional subscriptions required for testing
   - Live data subscriptions are not available in paper trading

## Configuration Steps

### Step 1: Configure IB Gateway/TWS for Paper Trading

1. **Launch IB Gateway (Paper Trading)**
   - Make sure you're using the **Paper Trading** version
   - Log in with your paper trading account credentials

2. **Enable API Access**
   - Go to **Configure** → **Settings** → **API** → **Settings**
   - Check **Enable ActiveX and Socket Clients**
   - Set **Socket Port** to `4002` (or your preferred port)
   - Check **Read-Only API** if you only want to test data retrieval (recommended for initial testing)
   - **Uncheck Read-Only API** if you want to test order placement (advanced)

3. **Configure Trusted IPs** (Optional but recommended)
   - Add `127.0.0.1` (localhost) to trusted IPs
   - This allows local connections without additional authentication

### Step 2: Configure MarketScanner App

Update `appsettings.json`:

```json
{
  "Ibkr": {
    "Host": "127.0.0.1",
    "Port": 4002,
    "ClientId": 7777,
    "MarketDataType": 3,
    "UseDelayedData": true
  }
}
```

**Key Settings:**
- `Host`: `127.0.0.1` (localhost)
- `Port`: Must match IB Gateway port (default `4002`)
- `ClientId`: Any unique number (default `7777`)
- `MarketDataType`: `3` = Delayed (required for paper trading)
- `UseDelayedData`: `true` (paper trading uses delayed data)

### Step 3: Verify Connection

1. **Start IB Gateway (Paper Trading)**
   - Ensure it's running and logged in
   - Check that API is enabled (green indicator)

2. **Run MarketScanner Application**
   - Launch the app
   - Check logs for connection status
   - Look for: `"Connected to IBKR (nextValidId=..., mode=DELAYED)"`

3. **Test Scanner**
   - Navigate to Scanner view
   - Click "Refresh" to test data retrieval
   - Verify stocks are loading correctly

## Testing the RSI Algorithm

### Step 1: Access Algorithm Runner

1. **Open Scanner View**
   - Select a stock from the scanner results
   - Double-click on a row or use the algorithm runner button

2. **Verify Algorithm Selection**
   - The algorithm runner should show "RSI Strategy"
   - Description should mention RSI-based trading

### Step 2: Test RSI Calculation

1. **Run Algorithm on a Symbol**
   - Select a symbol (e.g., AAPL, MSFT, TSLA)
   - Click "Run Algorithm"
   - Wait for execution (may take 5-10 seconds for historical data)

2. **Verify Results**
   - Check the algorithm result:
     - **Buy Signal**: RSI < 30 (oversold)
     - **Sell Signal**: RSI > 70 (overbought)
     - **Hold Signal**: RSI between 30-70 (neutral)
   - Review the reason message for RSI value and explanation

3. **Check Logs**
   - Look for log entries:
     - `"RSIAlgoStrategy: Executing for symbol {Symbol}"`
     - `"GetHistoricalBarsForRSIAsync: Received {Count} bars"`
     - `"RSIAlgoStrategy: Calculated RSI={RSI:F2}"`
     - `"RSIAlgoStrategy: {Action} signal for {Symbol}"`

### Step 3: Test Different Scenarios

#### Test Case 1: Oversold Condition (Buy Signal)
- **Target**: Find stocks with RSI < 30
- **Method**: Run algorithm on stocks that have been declining
- **Expected**: Buy signal with RSI value < 30

#### Test Case 2: Overbought Condition (Sell Signal)
- **Target**: Find stocks with RSI > 70
- **Method**: Run algorithm on stocks that have been rising
- **Expected**: Sell signal with RSI value > 70

#### Test Case 3: Neutral Condition (Hold Signal)
- **Target**: Find stocks with RSI between 30-70
- **Method**: Run algorithm on stable stocks
- **Expected**: Hold signal with RSI value in neutral range

#### Test Case 4: Insufficient Data
- **Target**: Test error handling
- **Method**: Try a symbol with limited history (new IPO, delisted stock)
- **Expected**: Hold signal with error message about insufficient data

#### Test Case 5: Connection Issues
- **Target**: Test error handling
- **Method**: Disconnect IB Gateway while algorithm is running
- **Expected**: Graceful error handling with timeout or connection error

## Monitoring and Validation

### What to Monitor

1. **Algorithm Execution Time**
   - Should complete within 10-15 seconds
   - Historical data request: ~5-10 seconds
   - RSI calculation: < 1 second

2. **Data Quality**
   - Verify historical bars are received (check logs)
   - Ensure bars are in chronological order
   - Confirm RSI values are between 0-100

3. **Signal Accuracy**
   - Compare RSI values with external sources (e.g., TradingView, Yahoo Finance)
   - Verify buy/sell signals match RSI thresholds
   - Check that reasons are descriptive and accurate

### Validation Checklist

- [ ] IB Gateway (Paper Trading) is running
- [ ] API connection established (check logs)
- [ ] Historical data requests succeed
- [ ] RSI calculation completes without errors
- [ ] RSI values are reasonable (0-100 range)
- [ ] Trading signals match RSI thresholds
- [ ] Error handling works for edge cases
- [ ] Logs provide sufficient debugging information

## Common Issues and Solutions

### Issue 1: Connection Failed
**Symptoms**: "Not connected" or "Connection refused" errors

**Solutions**:
- Verify IB Gateway is running and logged in
- Check that API is enabled in IB Gateway settings
- Verify port number matches configuration (default 4002)
- Ensure firewall isn't blocking localhost connections
- Try restarting IB Gateway

### Issue 2: No Historical Data
**Symptoms**: "No historical data available" or timeout errors

**Solutions**:
- Verify market data subscriptions in paper trading account
- Check that symbol is valid and tradeable
- Ensure sufficient historical data exists (30+ days)
- Try a different symbol (e.g., AAPL, MSFT)
- Check IB Gateway logs for API errors

### Issue 3: RSI Calculation Errors
**Symptoms**: "Insufficient data" errors

**Solutions**:
- Ensure at least 15 bars are received (14-period RSI + 1)
- Check that bars contain valid close prices
- Verify historical data request returns complete bars
- Try increasing `HISTORICAL_DAYS` in RSIAlgoStrategy

### Issue 4: Slow Performance
**Symptoms**: Algorithm takes > 30 seconds

**Solutions**:
- Check network connection
- Verify IB Gateway is not overloaded
- Reduce historical data days (if acceptable)
- Check for multiple concurrent requests
- Review IB Gateway performance settings

## Advanced Testing

### Testing Order Placement (Optional)

**Warning**: Only test if you understand order placement risks, even in paper trading.

1. **Disable Read-Only API**
   - In IB Gateway: Configure → Settings → API → Settings
   - Uncheck "Read-Only API"

2. **Implement Order Placement** (Future Enhancement)
   - Add order placement logic to algorithm
   - Test with small paper trading orders
   - Monitor order execution and fills

3. **Risk Management**
   - Set position size limits
   - Implement stop-loss orders
   - Monitor account balance

### Performance Testing

1. **Concurrent Requests**
   - Test running algorithm on multiple symbols simultaneously
   - Monitor for rate limiting or connection issues

2. **Stress Testing**
   - Run algorithm on 10+ symbols in sequence
   - Verify no memory leaks or resource exhaustion

3. **Long-Running Tests**
   - Run algorithm continuously for 1+ hours
   - Monitor for connection stability
   - Check for memory usage growth

## Best Practices

1. **Always Use Paper Trading First**
   - Never test with real money
   - Paper trading simulates real market conditions
   - Validate algorithm logic before live trading

2. **Monitor Logs**
   - Enable verbose logging during testing
   - Review logs for errors and warnings
   - Keep logs for debugging issues

3. **Validate Results**
   - Compare RSI values with external sources
   - Verify signals make logical sense
   - Test edge cases and error conditions

4. **Start Small**
   - Test with well-known symbols (AAPL, MSFT)
   - Verify basic functionality first
   - Gradually test more complex scenarios

5. **Document Issues**
   - Keep notes of any problems encountered
   - Document solutions for future reference
   - Share findings with team (if applicable)

## Next Steps After Testing

Once paper trading tests are successful:

1. **Review Algorithm Performance**
   - Analyze signal accuracy
   - Review RSI calculation correctness
   - Evaluate execution speed

2. **Optimize if Needed**
   - Adjust RSI thresholds if signals are too frequent/rare
   - Fine-tune historical data period
   - Improve error handling

3. **Consider Additional Features**
   - Multiple timeframe RSI (e.g., 14-day and 30-day)
   - RSI divergence detection
   - Combined with other indicators (MACD, Moving Averages)

4. **Prepare for Live Trading** (If Applicable)
   - Understand real market data subscription costs
   - Set up proper risk management
   - Start with small position sizes
   - Monitor closely in initial live trading

## Resources

- [IBKR Paper Trading](https://www.interactivebrokers.com/en/index.php?f=16042)
- [IB Gateway Download](https://www.interactivebrokers.com/en/index.php?f=16457)
- [TWS API Documentation](https://www.interactivebrokers.com/campus/ibkr-api-page/twsapi-doc/)
- [RSI Indicator Explanation](https://www.investopedia.com/terms/r/rsi.asp)

## Support

If you encounter issues:
1. Check IB Gateway logs
2. Review MarketScanner application logs
3. Verify configuration settings
4. Consult IBKR API documentation
5. Test with different symbols
6. Restart IB Gateway and application

---

**Remember**: Paper trading is for testing only. Always validate your algorithm thoroughly before considering live trading.

