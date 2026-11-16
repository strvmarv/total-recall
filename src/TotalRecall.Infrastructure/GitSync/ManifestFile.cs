using System.Text;
using System.Text.Json;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The fields of a <c>total-recall/v1</c> <c>manifest.json</c>.</summary>
public sealed record ParsedManifest(string Format, string RepoId, string? CreatedBy, string? CreatedAt);

public enum ManifestParseKind { Ok, Newer, Invalid }

/// <summary>The result of reading a manifest: the fields, or the newer version number, or the reason it was refused.</summary>
public sealed record ManifestParse(ManifestParseKind Kind, ParsedManifest? Manifest, int NewerVersion, string? Reason)
{
    internal static ManifestParse Ok(ParsedManifest manifest) => new(ManifestParseKind.Ok, manifest, 0, null);
    internal static ManifestParse NewerFormat(int version) => new(ManifestParseKind.Newer, null, version, null);
    internal static ManifestParse Invalid(string reason) => new(ManifestParseKind.Invalid, null, 0, reason);
}

/// <summary>
/// Reads and renders the repository's <c>manifest.json</c> (spec decisions 1 and 2). The rules (format classes, the
/// shape of the id, the exact bytes) are in <c>TotalRecall.Core.SyncManifest</c>; this class reads the JSON.
/// </summary>
public static class ManifestFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The bytes of a <c>v1</c> manifest (the Core function; throws <see cref="ArgumentException"/> for bad input).</summary>
    public static byte[] Render(string repoId, string createdBy, string createdAt) =>
        SyncManifest.render(repoId, createdBy, createdAt);

    /// <summary>
    /// A newer format wins over every refusal except non-JSON and invalid UTF-8: after a strict UTF-8 check, a
    /// permissive look for a root <c>format</c> string (a byte-order mark ignored, the first of a duplicated key
    /// used) that names a newer format returns <c>Newer</c> whatever else is wrong. Only then are the 4 KiB cap and
    /// the <c>v1</c> rules applied. A span over 64 KiB is refused: the caller never reads more.
    /// </summary>
    public static ManifestParse Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SyncManifest.MaxLookBytes)
            return ManifestParse.Invalid("the manifest is larger than 64 KiB");

        try { _ = StrictUtf8.GetString(bytes); }
        catch (ArgumentException) { return ManifestParse.Invalid("the manifest is not valid UTF-8"); }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = hasBom ? bytes[3..] : bytes;

        var look = LookForFormat(body);
        if (look is not null && SyncManifest.classifyFormat(look) is { IsNewer: true } newer)
            return ManifestParse.NewerFormat(((SyncManifest.ManifestFormat.Newer)newer).Item);

        if (hasBom) return ManifestParse.Invalid("the manifest starts with a byte-order mark");
        if (bytes.Length > SyncManifest.MaxManifestBytes) return ManifestParse.Invalid("the manifest is larger than 4 KiB");
        return ParseStrict(bytes);
    }

    private static string? LookForFormat(ReadOnlySpan<byte> body)
    {
        string? format = null;
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read()) break;
                if (name == "format" && format is null && reader.TokenType == JsonTokenType.String)
                    format = reader.GetString();
                reader.Skip();
            }
        }
        catch (JsonException) { /* what was read so far stands */ }
        return format;
    }

    private static ManifestParse ParseStrict(ReadOnlySpan<byte> bytes)
    {
        string? format = null, repoId = null, createdBy = null, createdAt = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return ManifestParse.Invalid("the manifest is not a JSON object");
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                if (!seen.Add(name)) return ManifestParse.Invalid($"the manifest repeats the key \"{name}\"");
                if (!reader.Read()) break;
                switch (name)
                {
                    case "format":
                    case "repo_id":
                    case "created_by":
                    case "created_at":
                        if (reader.TokenType != JsonTokenType.String)
                            return ManifestParse.Invalid($"\"{name}\" is not a string");
                        var value = reader.GetString();
                        if (name == "format") format = value;
                        else if (name == "repo_id") repoId = value;
                        else if (name == "created_by") createdBy = value;
                        else createdAt = value;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject) return ManifestParse.Invalid("the manifest is not a complete JSON object");
            if (reader.Read()) return ManifestParse.Invalid("the manifest has content after its JSON object");
        }
        catch (JsonException)
        {
            return ManifestParse.Invalid("the manifest is not valid JSON");
        }

        if (format is null) return ManifestParse.Invalid("the manifest has no \"format\"");
        if (!SyncManifest.classifyFormat(format).IsSupported) return ManifestParse.Invalid("the manifest is not a total-recall/v1 manifest");
        if (repoId is null || !SyncManifest.isValidRepoId(repoId)) return ManifestParse.Invalid("the manifest's \"repo_id\" is not 32 lowercase hex digits");
        return ManifestParse.Ok(new ParsedManifest(format, repoId, createdBy, createdAt));
    }
}
