// src/TotalRecall.Server/Handlers/MetadataHelpers.cs
//
// Plan 6 Task 6.0b — small JSON helpers shared by KB admin handlers.
// Both KbListCollectionsHandler and KbRefreshHandler need to read string
// fields out of an Entry.MetadataJson string. Hand-rolled rather than
// shoehorned into a source-gen DTO because the metadata blob is open-ended
// (the TS side is `Record<string, unknown>`).

using System.Text.Json;

namespace TotalRecall.Server.Handlers;

internal static class MetadataHelpers
{
    /// <summary>
    /// Extract a string-valued property from an entry's metadata_json blob.
    /// Returns null if the JSON is missing/invalid, or if the property is
    /// absent or non-string. Mirrors the helper duplicated across the CLI
    /// kb commands (ListCommand, RefreshCommand, RemoveCommand).
    /// </summary>
    public static string? ExtractString(string? json, string propName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(propName, out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
