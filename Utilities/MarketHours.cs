using System;

namespace MarketScanner.Utilities;

/// <summary>
/// Utility class for determining US stock market session status.
/// </summary>
public static class MarketHours
{
    /// <summary>
    /// Represents the current market session status.
    /// </summary>
    public enum MarketStatus
    {
        Closed,      // Market is closed (overnight)
        Premarket,   // Pre-market hours (4:00 AM - 9:30 AM ET)
        Open,        // Regular trading hours (9:30 AM - 4:00 PM ET)
        AfterHours   // After-hours (4:00 PM - 8:00 PM ET)
    }

    /// <summary>
    /// Gets the Eastern Time timezone, handling cross-platform differences.
    /// </summary>
    private static TimeZoneInfo GetEasternTimeZone()
    {
        try
        {
            // Windows
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch
        {
            try
            {
                // Linux/Mac
                return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
            catch
            {
                // Fallback: create custom timezone (EST is UTC-5, EDT is UTC-4)
                // For simplicity, use UTC-5 as default (EST)
                return TimeZoneInfo.CreateCustomTimeZone("ET", TimeSpan.FromHours(-5), "Eastern Time", "ET");
            }
        }
    }

    /// <summary>
    /// Gets the current market status for US stocks.
    /// Uses Eastern Time (ET) for market hours.
    /// </summary>
    /// <param name="utcTime">Optional UTC time. If not provided, uses current UTC time.</param>
    /// <returns>Current market status</returns>
    public static MarketStatus GetMarketStatus(DateTime? utcTime = null)
    {
        var now = utcTime ?? DateTime.UtcNow;
        
        // Convert UTC to Eastern Time
        var easternTimeZone = GetEasternTimeZone();
        var easternTime = TimeZoneInfo.ConvertTimeFromUtc(now, easternTimeZone);
        
        var timeOfDay = easternTime.TimeOfDay;
        var dayOfWeek = easternTime.DayOfWeek;
        
        // Market is closed on weekends
        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
        {
            return MarketStatus.Closed;
        }
        
        // Premarket: 4:00 AM - 9:30 AM ET
        if (timeOfDay >= new TimeSpan(4, 0, 0) && timeOfDay < new TimeSpan(9, 30, 0))
        {
            return MarketStatus.Premarket;
        }
        
        // Regular trading hours: 9:30 AM - 4:00 PM ET
        if (timeOfDay >= new TimeSpan(9, 30, 0) && timeOfDay < new TimeSpan(16, 0, 0))
        {
            return MarketStatus.Open;
        }
        
        // After-hours: 4:00 PM - 8:00 PM ET
        if (timeOfDay >= new TimeSpan(16, 0, 0) && timeOfDay < new TimeSpan(20, 0, 0))
        {
            return MarketStatus.AfterHours;
        }
        
        // Closed: 8:00 PM - 4:00 AM ET
        return MarketStatus.Closed;
    }

    /// <summary>
    /// Gets a user-friendly string representation of the market status.
    /// </summary>
    public static string GetMarketStatusString(MarketStatus status)
    {
        return status switch
        {
            MarketStatus.Closed => "Market Closed",
            MarketStatus.Premarket => "Pre-Market",
            MarketStatus.Open => "Market Open",
            MarketStatus.AfterHours => "After Hours",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Gets the next market open time in UTC.
    /// </summary>
    public static DateTime? GetNextMarketOpen(DateTime? fromUtc = null)
    {
        var from = fromUtc ?? DateTime.UtcNow;
        var easternTimeZone = GetEasternTimeZone();
        var easternTime = TimeZoneInfo.ConvertTimeFromUtc(from, easternTimeZone);
        
        var nextOpen = easternTime.Date.AddHours(9).AddMinutes(30);
        
        // If we're past today's open, move to next trading day
        if (easternTime >= nextOpen)
        {
            nextOpen = nextOpen.AddDays(1);
        }
        
        // Skip weekends
        while (nextOpen.DayOfWeek == DayOfWeek.Saturday || nextOpen.DayOfWeek == DayOfWeek.Sunday)
        {
            nextOpen = nextOpen.AddDays(1);
        }
        
        // Convert back to UTC
        return TimeZoneInfo.ConvertTimeToUtc(nextOpen, easternTimeZone);
    }
}

