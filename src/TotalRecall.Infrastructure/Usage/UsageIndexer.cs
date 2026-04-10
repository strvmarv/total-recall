// src/TotalRecall.Infrastructure/Usage/UsageIndexer.cs
//
// Orchestrator: iterates registered IUsageImporter adapters, streams
// their events into UsageEventLog, and advances per-host watermarks
// on success. Invoked once per session_start.
//
// Failure isolation: per-host exceptions are caught, logged via the
// existing ExceptionLogger pattern, and DO NOT advance the watermark
// for that host — the next session_start retries cleanly. Other hosts
// continue scanning. This matches SessionLifecycle's existing "don't
// block session init on a single importer failure" policy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Usage;

public sealed class UsageIndexer
{
    private readonly IReadOnlyList<IUsageImporter> _importers;
    private readonly UsageEventLog _eventLog;
    private readonly UsageWatermarkStore _watermarks;
    private readonly TextWriter _stderr;

    public UsageIndexer(
        IReadOnlyList<IUsageImporter> importers,
        UsageEventLog eventLog,
        UsageWatermarkStore watermarks,
        TextWriter? stderr = null)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(watermarks);
        _importers = importers;
        _eventLog = eventLog;
        _watermarks = watermarks;
        _stderr = stderr ?? Console.Error;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var importer in _importers)
        {
            if (!importer.Detect()) continue;

            var since = _watermarks.GetLastIndexedTs(importer.HostName);
            var newMax = since;
            var inserted = 0;

            try
            {
                await foreach (var evt in importer.ScanAsync(since, ct).ConfigureAwait(false))
                {
                    _eventLog.InsertOrIgnore(evt);
                    if (evt.TimestampMs > newMax) newMax = evt.TimestampMs;
                    inserted++;
                }
            }
            catch (OperationCanceledException)
            {
                // Propagate cooperative cancellation cleanly — do NOT log as "scan
                // failed" and do NOT continue to the next importer. Callers that
                // cancel expect RunAsync to unwind promptly (e.g., on shutdown).
                throw;
            }
            catch (Exception ex)
            {
                ExceptionLogger.LogChain(
                    _stderr,
                    $"total-recall: usage indexer: {importer.HostName} scan failed",
                    ex);
                continue; // other hosts still run; watermark for this host stays put
            }

            if (newMax > since)
            {
                _watermarks.SetLastIndexedTs(importer.HostName, newMax);
            }

            if (inserted > 0)
            {
                _stderr.WriteLine(
                    $"total-recall: usage indexer: scanned {inserted} new events from {importer.HostName}");
            }
        }
    }
}
