using System;
using System.Security.Cryptography;
using System.Text;

namespace TotalRecall.Infrastructure.Importers;

/// <summary>
/// Entry ids derived from an import's content hash. Two machines that import
/// the same host memory (same file text, same Hermes section) compute the same
/// id, so git sync sees one record instead of two. Only memory imports use it;
/// knowledge imports keep random ids.
/// </summary>
public static class ImportIds
{
    /// <summary>
    /// The first 16 bytes of <c>SHA-256(UTF-8(contentHash))</c> as a lowercase
    /// <c>8-4-4-4-12</c> hex string. The input is the same hash the import log
    /// and <c>IsAlreadyImported</c> use. The result is not an RFC 4122 UUID (no
    /// version or variant bits); it only has to look like the ids the store
    /// already creates and match the sync id rule
    /// <c>^[0-9a-f][0-9a-f-]{7,63}$</c>.
    /// </summary>
    public static string FromHash(string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(contentHash));
        var hex = Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
