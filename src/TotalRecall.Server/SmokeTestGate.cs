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

    public SmokeTestGate(
        IMetaStore meta,
        string version,
        Func<CancellationToken, Task<BenchmarkResult>> run,
        string metaKey = MetaKey)
    {
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _metaKey = metaKey;
    }

    /// <summary>
    /// Runs the smoke test when none has completed for this version. Returns
    /// null when it is skipped.
    /// </summary>
    public SmokeTestResult? RunIfNeeded()
    {
        // An unstamped build cannot be gated: skip rather than run every session.
        if (string.IsNullOrWhiteSpace(_version) || _version == UnknownVersion) return null;
        if (string.Equals(_meta.GetMeta(_metaKey), _version, StringComparison.Ordinal)) return null;

        var result = _run(CancellationToken.None).GetAwaiter().GetResult();
        _meta.SetMeta(_metaKey, _version);
        return Map(result);
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
