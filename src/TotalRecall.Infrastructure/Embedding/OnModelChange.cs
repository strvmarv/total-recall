namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Policy for what the server does when the stored embedder fingerprint no longer
/// matches the configured embedder (configured via <c>embedding.on_model_change</c>).
/// </summary>
public enum OnModelChange
{
    /// <summary>Re-embed every row in place + restamp, then continue (the safe default).</summary>
    Auto,

    /// <summary>Log a warning and continue degraded, without restamping.</summary>
    Warn,

    /// <summary>Refuse to start — throw the fingerprint-mismatch exception (legacy fail-fast).</summary>
    Block,
}

/// <summary>
/// Parses the <c>embedding.on_model_change</c> config string into an
/// <see cref="OnModelChange"/> policy.
/// </summary>
public static class OnModelChangePolicy
{
    /// <summary>
    /// Parse the config string. Case-insensitive. <c>null</c>/empty/unknown →
    /// <see cref="OnModelChange.Auto"/> (the safe, non-blocking default).
    /// </summary>
    public static OnModelChange Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OnModelChange.Auto;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "warn" => OnModelChange.Warn,
            "block" => OnModelChange.Block,
            _ => OnModelChange.Auto,
        };
    }
}
