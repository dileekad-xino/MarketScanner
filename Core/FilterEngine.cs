using System;
using System.Linq;
using System.Runtime.CompilerServices;
using MarketScanner.Models;
using MarketScanner.ViewModels;
using Microsoft.Extensions.Logging;


namespace MarketScanner.Core
{
    public static class FilterEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Matches(string? rowValue, string? criterion)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return true;
            if (string.IsNullOrWhiteSpace(rowValue)) return false;
            return string.Equals(rowValue.Trim(), criterion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public sealed record Criteria(
            string? Exchange,  // Region, Product, and RelativeVolume removed
            decimal? MinPrice, decimal? MaxPrice,
            decimal? MinChgPct,
            long? MinVolume,
            int TopN);
        public sealed record Result(int[] TopIndices);

        public static Result Apply(ScannerRowViewModel[] rows, Criteria c, ILogger? logger = null)
        {
            // filter
            var idx = Enumerable.Range(0, rows.Length).Where(i =>
            {
                var r = rows[i];

                // Price may be pending briefly after refresh; volume should still be filterable.
                bool hasPendingPrice = r.LastPrice == 0;
                long effectiveVolume = r.Volume > 0 ? r.Volume : r.AvgVolume;

                // Apply metadata filters
                if (!Matches(r.Exchange, c.Exchange)) return false;

                // Price filters - skip only while price is pending.
                if (!hasPendingPrice)
                {
                    if (c.MinPrice is { } pmin && r.LastPrice < (double)pmin) return false;
                    if (c.MaxPrice is { } pmax && r.LastPrice > (double)pmax) return false;
                }

                // Volume filter is always enforced.
                if (c.MinVolume is { } vmin && effectiveVolume < vmin) return false;

                // User-defined filters - ALWAYS apply (uses live tick data)
                // MinChgPct comparison: both values are percentages (e.g., 9.0 means 9%)
                if (c.MinChgPct is { } cmin)
                {
                    logger?.LogInformation("Comparing {Symbol}: MinChgPct={MinChgPct} vs ChangePercent={ChangePercent} => Pass={Pass}", 
                        r.Symbol, c.MinChgPct, r.ChangePercent, r.ChangePercent >= (double)cmin);
                    if (r.ChangePercent < (double)cmin) return false;
                }

                return true;
            });

            // default sort: Change % desc
            var ordered = idx.OrderByDescending(i => rows[i].ChangePercent);

            // TopN (guard)
            var top = (c.TopN > 0 ? ordered.Take(c.TopN) : ordered).ToArray();
            return new Result(top);
        }
    }
}
