using System;
using System.Globalization;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Thrown when the configured embedder does not match the fingerprint
/// stamped in <c>_meta</c> on first store open. Indicates vectors already
/// in the DB were produced by a different model — continuing would mix
/// embedding spaces and silently degrade retrieval quality.
/// </summary>
public sealed class EmbedderFingerprintMismatchException : InvalidOperationException
{
    public EmbedderDescriptor Stored { get; }
    public EmbedderDescriptor Configured { get; }

    public EmbedderFingerprintMismatchException(
        EmbedderDescriptor stored,
        EmbedderDescriptor configured)
        : base(BuildMessage(stored, configured))
    {
        Stored = stored;
        Configured = configured;
    }

    private static string BuildMessage(EmbedderDescriptor stored, EmbedderDescriptor configured) =>
        "Embedder fingerprint mismatch: this database was initialized with " +
        $"provider={stored.Provider}, model={stored.Model}, revision={Quote(stored.Revision)}, dimensions={stored.Dimensions}, " +
        "but the current configuration uses " +
        $"provider={configured.Provider}, model={configured.Model}, revision={Quote(configured.Revision)}, dimensions={configured.Dimensions}. " +
        "Continuing would mix embedding spaces and silently degrade retrieval quality. " +
        "Either restore the original embedder configuration, or rebuild the database from a fresh path " +
        "(e.g. by pointing TOTAL_RECALL_DB_PATH at a new file) and re-ingest your memories.";

    private static string Quote(string s) => string.IsNullOrEmpty(s) ? "(none)" : s;
}

/// <summary>
/// Reads and writes the embedder identity fingerprint in the <c>_meta</c>
/// table. On first open (empty fingerprint) the configured embedder is
/// stamped; on subsequent opens the configured embedder is compared to
/// the stamped fingerprint and a <see cref="EmbedderFingerprintMismatchException"/>
/// is thrown on any difference.
///
/// This closes the silent-failure window where swapping to a different
/// embedding model with the same dimensionality (e.g. all-MiniLM-L6-v2 →
/// OpenAI text-embedding-3-small at 384 dims) would leave the existing
/// 384-dim vectors in place while new vectors are produced in a different
/// semantic space. Dimension mismatches already fail loudly at the vec0 /
/// pgvector layer; same-dimension model swaps did not.
/// </summary>
public static class EmbedderFingerprint
{
    internal const string KeyProvider = "embed.provider";
    internal const string KeyModel = "embed.model";
    internal const string KeyRevision = "embed.revision";
    internal const string KeyDimensions = "embed.dimensions";

    /// <summary>
    /// If no fingerprint is stamped, stamp the configured embedder's
    /// descriptor. If a fingerprint is stamped, compare it to the configured
    /// embedder and throw on mismatch.
    /// </summary>
    public static void EnsureMatches(IMetaStore meta, IEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(embedder);

        var configured = embedder.Descriptor;

        var stored = ReadStored(meta);
        if (stored is null)
        {
            Stamp(meta, configured);
            return;
        }

        if (!Equal(stored, configured))
        {
            throw new EmbedderFingerprintMismatchException(stored, configured);
        }
    }

    private static EmbedderDescriptor? ReadStored(IMetaStore meta)
    {
        var provider = meta.GetMeta(KeyProvider);
        var model = meta.GetMeta(KeyModel);
        var revision = meta.GetMeta(KeyRevision);
        var dimsRaw = meta.GetMeta(KeyDimensions);

        // Treat any missing key as "no fingerprint". Partial fingerprints
        // (e.g. from a failed earlier stamp) are restamped from the current
        // configuration rather than treated as a mismatch — this avoids
        // locking users out of a DB that never had a complete stamp.
        if (provider is null || model is null || revision is null || dimsRaw is null)
        {
            return null;
        }

        if (!int.TryParse(dimsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dims))
        {
            return null;
        }

        return new EmbedderDescriptor(provider, model, revision, dims);
    }

    private static void Stamp(IMetaStore meta, EmbedderDescriptor desc)
    {
        meta.SetMeta(KeyProvider, desc.Provider);
        meta.SetMeta(KeyModel, desc.Model);
        meta.SetMeta(KeyRevision, desc.Revision);
        meta.SetMeta(
            KeyDimensions,
            desc.Dimensions.ToString(CultureInfo.InvariantCulture));
    }

    private static bool Equal(EmbedderDescriptor a, EmbedderDescriptor b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.Ordinal) &&
        string.Equals(a.Model, b.Model, StringComparison.Ordinal) &&
        string.Equals(a.Revision, b.Revision, StringComparison.Ordinal) &&
        a.Dimensions == b.Dimensions;
}
