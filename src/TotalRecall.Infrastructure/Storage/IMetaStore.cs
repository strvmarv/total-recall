namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Narrow key/value accessor over the <c>_meta</c> table. Implemented by
/// both <see cref="SqliteStore"/> and <see cref="PostgresStore"/> so the
/// embedder fingerprint guard can stamp and verify the configured
/// embedding model on store open without taking a dependency on the
/// full <see cref="IStore"/> surface or on either concrete backend.
/// </summary>
public interface IMetaStore
{
    /// <summary>
    /// Read a <c>_meta</c> value by key. Returns <c>null</c> if the key is
    /// not present.
    /// </summary>
    string? GetMeta(string key);

    /// <summary>
    /// Upsert a <c>_meta</c> key/value pair. Overwrites any existing value
    /// for the same key.
    /// </summary>
    void SetMeta(string key, string value);
}
