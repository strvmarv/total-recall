// src/TotalRecall.Server/SmokeTestGate.cs
//
// The version-gated first-run smoke test reported in session_start's
// `smokeTest` field. The gate decides whether the smoke queries run, runs the
// injected benchmark delegate, and maps the benchmark result to a
// SmokeTestResult. The version is recorded in `_meta` only after a run
// COMPLETES (pass or threshold miss); a run that cannot complete is retried
// on the next session_start.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server;

/// <summary>See the file header.</summary>
public sealed class SmokeTestGate
{
    /// <summary>The <c>_meta</c> key that holds the last version a smoke test completed for.</summary>
    public const string MetaKey = "smoke_test_version";

    /// <summary>Minimum exact-match rate for a pass (the TypeScript engine's threshold).</summary>
    public const double PassThreshold = 0.8;

    /// <summary><c>McpServer.ResolveServerVersion</c> returns this when no version can be read.</summary>
    public const string UnknownVersion = "unknown";

    private readonly IMetaStore _meta;
    private readonly string _version;
    private readonly Func<CancellationToken, Task<BenchmarkResult>> _run;
    private readonly string _metaKey;
    private readonly TimeSpan _timeout;

    /// <summary>Default cap on one smoke run.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public SmokeTestGate(
        IMetaStore meta,
        string version,
        Func<CancellationToken, Task<BenchmarkResult>> run,
        string metaKey = MetaKey,
        TimeSpan? timeout = null)
    {
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _metaKey = metaKey;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Runs the smoke test when none has completed for this version. Returns
    /// null when it is skipped. Never throws: a run that cannot complete (it
    /// throws, a bundled file is missing, or the cap cancels it) is reported as
    /// <c>passed=false</c> with the reason and records nothing, so the next
    /// session_start tries again.
    /// </summary>
    public SmokeTestResult? RunIfNeeded()
    {
        try
        {
            // An unstamped build cannot be gated: skip rather than run every session.
            if (string.IsNullOrWhiteSpace(_version) || _version == UnknownVersion) return null;
            if (string.Equals(_meta.GetMeta(_metaKey), _version, StringComparison.Ordinal)) return null;

            BenchmarkResult result;
            using (var cts = new CancellationTokenSource())
            {
                // The runner is cooperative: it checks the token between steps, so a
                // step already in progress (a model load, one embed) can overshoot.
                // Awaiting the task, not abandoning it, keeps the shared embedder and
                // the temp database free of a background run.
                cts.CancelAfter(_timeout);
                try
                {
                    result = _run(cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return new SmokeTestResult(false,
                        $"smoke test did not finish within {_timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s and was cancelled");
                }
            }

            // An empty or truncated bundled smoke.jsonl runs no queries: that is a
            // broken bundle, not a threshold miss, so nothing is recorded.
            if (result.TotalQueries <= 0)
                return new SmokeTestResult(false, "smoke test could not run: no smoke queries ran (the bundled benchmark file is empty)");

            var mapped = Map(result);
            RecordVersion();
            return mapped;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: smoke test could not run: {ex.Message}");
            return new SmokeTestResult(false, $"smoke test could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// Records the version after a completed run. A write failure (for example a
    /// locked database) is logged and never turns the real result into a failure.
    /// </summary>
    private void RecordVersion()
    {
        try { _meta.SetMeta(_metaKey, _version); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: could not record the smoke test version: {ex.Message}");
        }
    }

    private static SmokeTestResult Map(BenchmarkResult r)
    {
        var rate = Percent(r.ExactMatchRate);
        var threshold = Percent(PassThreshold);
        return r.ExactMatchRate >= PassThreshold
            ? new SmokeTestResult(true, $"{r.TotalQueries} queries, exact match {rate} (threshold {threshold})")
            : new SmokeTestResult(false, $"exact match rate {rate} is below the {threshold} threshold ({r.TotalQueries} queries)");
    }

    private static string Percent(double v)
        => (v * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
