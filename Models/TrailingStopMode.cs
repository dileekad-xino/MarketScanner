namespace MarketScanner.Models;

/// <summary>
/// Trailing stop distance calculation mode
/// </summary>
public enum TrailingStopMode
{
    /// <summary>
    /// Distance is specified as a percentage (e.g., 0.5%, 1%, 3%)
    /// </summary>
    Percentage,
    
    /// <summary>
    /// Distance is specified as a price amount (e.g., $0.02, $0.10, $0.50)
    /// </summary>
    Price
}

