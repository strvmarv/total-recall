// src/TotalRecall.Infrastructure/Embedding/ModelNotReadyException.cs
//
// Plan 4 Task 4.5 — strongly-typed exception that mirrors the TS
// `ModelNotReadyError` (src-ts/embedding/errors.ts). The .NET server's
// ErrorTranslator special-cases this exception type to return a friendly
// MCP payload that tells the host *why* the embedding model is unavailable
// and (optionally) how far along a download is. Producing this exception
// is a known, recoverable state — not a server error — so the translator
// does NOT log it to stderr.
//
// TODO(Plan 5+): wire OnnxEmbedder + ModelManager to throw this exception
// for missing/downloading/failed/corrupted model states. Task 4.5 only
// introduces the type so Server can handle it; the producers stay untouched.

namespace TotalRecall.Infrastructure.Embedding;

using System;

public enum ModelNotReadyReason
{
    Missing,
    Downloading,
    Failed,
    Corrupted,
}

public sealed class ModelNotReadyException : Exception
{
    public string ModelName { get; }
    public ModelNotReadyReason Reason { get; }
    public string? Hint { get; }

    public ModelNotReadyException(
        string modelName,
        ModelNotReadyReason reason,
        string? hint = null,
        Exception? innerException = null)
        : base(BuildMessage(modelName, reason, hint), innerException)
    {
        ModelName = modelName;
        Reason = reason;
        Hint = hint;
    }

    private static string BuildMessage(string modelName, ModelNotReadyReason reason, string? hint)
    {
        var @base = $"Model '{modelName}' not ready: {reason.ToString().ToLowerInvariant()}";
        return hint is null ? @base : $"{@base} ({hint})";
    }
}
