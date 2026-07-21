using System;
using System.Collections.Generic;

namespace TaskbarQuota.Cost
{
    /// <summary>
    /// Reads a provider's local logs and yields raw token-usage events. Scanners never price —
    /// they only extract <see cref="TokenUsageRecord"/>s; pricing is applied centrally by
    /// <see cref="CostService"/> so every provider uses the one canonical API-list catalog.
    /// </summary>
    public interface ICostScanner
    {
        Usage.ProviderId Provider { get; }

        /// <summary>
        /// Enumerate usage events on/after <paramref name="sinceUtc"/>. Implementations must be
        /// resilient to partially written / malformed lines (logs are appended live) and should
        /// simply skip anything they can't parse rather than throwing.
        /// </summary>
        IEnumerable<TokenUsageRecord> Scan(DateTimeOffset sinceUtc);
    }
}
