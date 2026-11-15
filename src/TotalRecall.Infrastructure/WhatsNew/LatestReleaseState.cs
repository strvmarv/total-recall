// src/TotalRecall.Infrastructure/WhatsNew/LatestReleaseState.cs
//
// The newest release the update checker saw per channel, under <dataDir>/whats-new/:
//   latest-stable.json / latest-pre.json
//   {"version":"4.9.0","summary":"...","checkedAt":"<ISO-8601 UTC>","lastAttempt":"<ISO-8601 UTC>"}
// Each key is omitted when it has no value. The file is untrusted on read (a hand edit, or a file
// written by another machine's clock): it is size-capped, the version must pass
// UpdateChannel.IsValidReleaseVersion and the summary is sanitised again, so nothing in it can
// lengthen or alter a notice line. Reads are fail-safe; writes are atomic and swallow IO errors.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Json;

namespace TotalRecall.Infrastructure.WhatsNew;

public sealed record LatestReleaseFile(
    string? Version, string? Summary, DateTimeOffset? CheckedAt, DateTimeOffset? LastAttempt);

public static class LatestReleaseState
{
    public const long MaxBytes = 64 * 1024;

    // The callers validate the channel; the file name is a fixed string per known channel.
    public static string FileName(string channel) => "latest-" + channel + ".json";

    public static LatestReleaseFile? Read(string dir, string channel)
    {
        if (!UpdateChannel.IsKnown(channel)) return null;
        try
        {
            var path = Path.Combine(dir, FileName(channel));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var version = GetString(root, "version");
            if (!UpdateChannel.IsValidReleaseVersion(version)) version = null;

            string? summary = null;
            if (version is not null)
            {
                var clean = SummarySanitizer.Sanitize(GetString(root, "summary"));
                if (clean.Length > 0) summary = clean;
            }

            return new LatestReleaseFile(version, summary,
                GetTime(root, "checkedAt"), GetTime(root, "lastAttempt"));
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string dir, string channel, LatestReleaseFile file)
    {
        if (!UpdateChannel.IsKnown(channel)) return;
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append('{');
            var first = true;
            Append(sb, ref first, "version", file.Version);
            Append(sb, ref first, "summary", file.Summary);
            Append(sb, ref first, "checkedAt", file.CheckedAt is { } c ? FormatTime(c) : null);
            Append(sb, ref first, "lastAttempt", file.LastAttempt is { } l ? FormatTime(l) : null);
            sb.Append('}');

            var path = Path.Combine(dir, FileName(channel));
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>ISO 8601 UTC with milliseconds and a literal Z, as JavaScript's toISOString.</summary>
    public static string FormatTime(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Append(StringBuilder sb, ref bool first, string key, string? value)
    {
        if (value is null) return;
        if (!first) sb.Append(',');
        first = false;
        JsonWriter.AppendString(sb, key); sb.Append(':');
        JsonWriter.AppendString(sb, value);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? GetTime(JsonElement root, string name)
    {
        var s = GetString(root, name);
        return s is not null
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }
}
