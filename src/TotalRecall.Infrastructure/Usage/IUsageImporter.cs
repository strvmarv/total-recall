// src/TotalRecall.Infrastructure/Usage/IUsageImporter.cs
//
// Adapter interface for reading usage events from a single host's log
// files. PURE — does NOT write to SQLite. Implementations stream
// UsageEvent records via IAsyncEnumerable; the UsageIndexer orchestrator
// owns all database writes. This mirrors the existing Importers/IImporter
// separation where parsers and writers are separated by concern.
//
// Watermark is passed IN via sinceMs, not stored in the adapter — the
// indexer holds the watermark in usage_watermarks and calls ScanAsync
// with the current value. Adapters are stateless and cheap to construct.

using System.Collections.Generic;
using System.Threading;

namespace TotalRecall.Infrastructure.Usage;

public interface IUsageImporter
{
    /// <summary>Host identifier written to usage_events.host.</summary>
    string HostName { get; }

    /// <summary>
    /// True if this host has data on this machine (e.g., the transcript
    /// directory exists). Cheap check; called on every UsageIndexer pass.
    /// </summary>
    bool Detect();

    /// <summary>
    /// Emit all UsageEvent records with ts &gt; sinceMs. Callers persist
    /// them via UsageEventLog. Ordering is not guaranteed — the writer
    /// is UNIQUE on (host, host_event_id) and handles duplicates via
    /// INSERT OR IGNORE.
    /// </summary>
    IAsyncEnumerable<UsageEvent> ScanAsync(long sinceMs, CancellationToken ct);
}
