using System;
using System.IO;
using Xunit;

namespace TotalRecall.TestSupport.Transcripts;

public enum BgeModelStatus
{
    /// <summary>The model file was found.</summary>
    Available,

    /// <summary>The model is missing and <c>CI</c> is not set: the test is skipped with a visible reason.</summary>
    Skip,

    /// <summary>The model is missing and <c>CI</c> is set: a skip would let the acceptance queries pass unrun.</summary>
    Fail,
}

/// <param name="Status">What a test should do.</param>
/// <param name="ModelPath">The full path of <c>model.onnx</c> when <see cref="BgeModelStatus.Available"/>, else null.</param>
/// <param name="Reason">The explanation when the model is missing, else null.</param>
public sealed record BgeModelResolution(BgeModelStatus Status, string? ModelPath, string? Reason);

/// <summary>
/// A fact that needs the real bge-small model (<c>models/bge-small-en-v1.5/model.onnx</c>, fetched by
/// <c>scripts/fetch-bge-small.sh</c>), found by walking up from the test assembly. With the model missing it skips with
/// a visible reason on a developer machine; with the environment variable <c>CI</c> set a missing model must fail, and a
/// FactAttribute cannot fail a test by itself, so the test body asks <see cref="RequireModelPath"/> (which it needs
/// anyway to build the embedder) and that throws. Uses only public API.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BgeModelFactAttribute : FactAttribute
{
    public const string SkipReason = "bge model not found (run scripts/fetch-bge-small.sh)";

    public BgeModelFactAttribute()
    {
        var r = Resolve(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
        if (r.Status == BgeModelStatus.Skip) Skip = r.Reason;
    }

    /// <summary>
    /// The rule, with the environment and the starting directory injected. <paramref name="getEnvironmentVariable"/> is
    /// asked for <c>CI</c>; any non-empty value counts as set.
    /// </summary>
    public static BgeModelResolution Resolve(Func<string, string?> getEnvironmentVariable, string startDirectory)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(startDirectory);

        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "bge-small-en-v1.5", "model.onnx");
            if (File.Exists(candidate)) return new BgeModelResolution(BgeModelStatus.Available, candidate, null);
            dir = dir.Parent;
        }

        var ci = !string.IsNullOrEmpty(getEnvironmentVariable("CI"));
        return new BgeModelResolution(ci ? BgeModelStatus.Fail : BgeModelStatus.Skip, null, SkipReason);
    }

    /// <summary>The path of <c>model.onnx</c>; throws (so the test fails) when it is missing, which only a run with <c>CI</c> set reaches.</summary>
    public static string RequireModelPath()
    {
        var r = Resolve(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
        return r.ModelPath ?? throw new InvalidOperationException(
            r.Reason + (r.Status == BgeModelStatus.Fail ? "; CI is set, so the test fails instead of skipping" : ""));
    }
}
