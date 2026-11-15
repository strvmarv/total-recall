// src/TotalRecall.Infrastructure/WhatsNew/PackagedReleaseNotes.cs
//
// Reads release-notes.json, the file scripts/release-notes.js writes next to the
// engine binary. Only version, previous and summary are taken from it (the URLs
// are built by the engine). Fail-safe: any problem is "no notes"; never throws,
// never deletes the file.

using System;
using System.IO;
using System.Text.Json;

namespace TotalRecall.Infrastructure.WhatsNew;

public sealed record PackagedNotes(string Version, string? Previous, string Summary);

public static class PackagedReleaseNotes
{
    public const string FileName = "release-notes.json";

    // The real file is well under 10 KB; anything larger is not ours.
    private const long MaxBytes = 1024 * 1024;

    public static PackagedNotes? Read(string binaryDir)
    {
        try
        {
            var path = Path.Combine(binaryDir, FileName);
            if (new FileInfo(path).Length > MaxBytes) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var version = GetString(root, "version");
            if (version is null) return null;

            return new PackagedNotes(version, GetString(root, "previous"), GetString(root, "summary") ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
