// src/TotalRecall.Infrastructure/Eval/RegressionOptions.cs
//
// Thresholds for session-start regression detection, resolved from the
// optional [regression] config section. An unset or invalid value takes the
// shipped default (the values in defaults.toml), so a hand-edited config can
// never make detection fire on every start or never fire.

using System;
using Microsoft.FSharp.Core;
using CoreConfig = TotalRecall.Core.Config;

namespace TotalRecall.Infrastructure.Eval;

/// <param name="MissRateDelta">Alert when the miss rate rose by at least this much (0 &lt; delta).</param>
/// <param name="LatencyRatio">Alert when average latency rose by at least this factor (ratio &gt;= 1).</param>
/// <param name="MinEvents">Scored events required on each side before a comparison is made (&gt;= 1).</param>
public sealed record RegressionOptions(double MissRateDelta, double LatencyRatio, int MinEvents)
{
    public const double DefaultMissRateDelta = 0.1;
    public const double DefaultLatencyRatio = 2.0;
    public const int DefaultMinEvents = 10;

    public static RegressionOptions Default { get; } =
        new(DefaultMissRateDelta, DefaultLatencyRatio, DefaultMinEvents);

    /// <summary>
    /// Resolve the options from the config's optional regression section
    /// (an F# option: null or None means the section is absent).
    /// </summary>
    public static RegressionOptions FromConfig(FSharpOption<CoreConfig.RegressionConfig>? section)
    {
        if (section is null || !FSharpOption<CoreConfig.RegressionConfig>.get_IsSome(section))
            return Default;

        var cfg = section.Value;
        var delta = Pick(cfg.MissRateDelta, DefaultMissRateDelta, v => double.IsFinite(v) && v > 0);
        var ratio = Pick(cfg.LatencyRatio, DefaultLatencyRatio, v => double.IsFinite(v) && v >= 1.0);
        var min = Pick(cfg.MinEvents, DefaultMinEvents, v => v >= 1);
        return new RegressionOptions(delta, ratio, min);
    }

    private static T Pick<T>(FSharpOption<T> value, T fallback, Func<T, bool> isValid)
    {
        if (value is null || !FSharpOption<T>.get_IsSome(value)) return fallback;
        return isValid(value.Value) ? value.Value : fallback;
    }
}
