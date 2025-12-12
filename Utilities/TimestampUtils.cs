using System;

namespace MarketScanner.Utilities;

/// <summary>
/// Utility class for timestamp normalization and timezone conversions.
/// Provides consistent timestamp handling across the application.
/// </summary>
public static class TimestampUtils
{
    /// <summary>
    /// Normalizes a DateTime to UTC, handling all DateTimeKind values.
    /// </summary>
    /// <param name="dt">The DateTime to normalize</param>
    /// <returns>DateTime in UTC with DateTimeKind.Utc</returns>
    public static DateTime NormalizeToUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return dt;
        
        if (dt.Kind == DateTimeKind.Local)
            return dt.ToUniversalTime();
        
        // DateTimeKind.Unspecified - assume it's already in UTC or treat as UTC
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>
    /// Normalizes to UTC and truncates to the nearest interval boundary (floor).
    /// Result is always DateTimeKind.Utc and aligned to interval boundaries.
    /// </summary>
    /// <param name="dt">The DateTime to normalize and truncate</param>
    /// <param name="intervalSeconds">The interval in seconds to truncate to</param>
    /// <returns>DateTime in UTC, truncated to interval boundary</returns>
    public static DateTime NormalizeAndTruncateToInterval(DateTime dt, int intervalSeconds)
    {
        var utc = NormalizeToUtc(dt);
        var interval = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        var truncatedTicks = (utc.Ticks / interval) * interval;
        return new DateTime(truncatedTicks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Checks if a timestamp is already truncated to the interval boundary.
    /// </summary>
    /// <param name="dt">The DateTime to check</param>
    /// <param name="intervalSeconds">The interval in seconds</param>
    /// <returns>True if the timestamp is truncated to the interval boundary</returns>
    public static bool IsTruncated(DateTime dt, int intervalSeconds)
    {
        if (dt.Kind != DateTimeKind.Utc)
            return false;
        
        var interval = TimeSpan.FromSeconds(intervalSeconds).Ticks;
        return (dt.Ticks % interval) == 0;
    }

    /// <summary>
    /// Gets the Eastern Time timezone, handling cross-platform differences.
    /// </summary>
    /// <returns>TimeZoneInfo for Eastern Time</returns>
    public static TimeZoneInfo GetEasternTimeZone()
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
    /// Converts DateTime.UtcNow to Eastern Time, then back to UTC.
    /// This ensures live tick timestamps match the format of historical data (ET converted to UTC).
    /// </summary>
    /// <returns>DateTime in UTC representing the current market time</returns>
    public static DateTime ConvertUtcNowToMarketTime()
    {
        var utcNow = DateTime.UtcNow;
        var easternTimeZone = GetEasternTimeZone();
        
        // Convert UTC to Eastern Time
        var easternTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternTimeZone);
        
        // Convert back to UTC (this ensures we have the correct market time in UTC)
        return TimeZoneInfo.ConvertTimeToUtc(easternTime, easternTimeZone);
    }
}

