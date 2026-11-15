// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewState.cs
//
// Per-host state files for the what's-new notice, under <dataDir>/whats-new/:
//   seen-<host>.json    {"version":"<bare version>"}   last version this host was told about
//   direct-<host>.json  {"at":"<ISO-8601 UTC>"}        stamp written by the host's own notice path
// A host reaches a file name only through HostIds.Normalize. Reads are fail-safe
// (any error is "no state"); writes are atomic and swallow IO errors.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;

namespace TotalRecall.Infrastructure.WhatsNew;

public static class WhatsNewState
{
    private static readonly TimeSpan StampMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan StampMaxFuture = TimeSpan.FromDays(1);

    public static string DefaultDir() => Path.Combine(ConfigLoader.GetDataDir(), "whats-new");

    public static string SeenFileName(string host) => "seen-" + HostIds.Normalize(host) + ".json";

    public static string StampFileName(string host) => "direct-" + HostIds.Normalize(host) + ".json";

    public static string? ReadSeenVersion(string dir, string host)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, SeenFileName(host))));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteSeenVersion(string dir, string host, string version)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append('{');
            JsonWriter.AppendString(sb, "version"); sb.Append(':');
            JsonWriter.AppendString(sb, version);
            sb.Append('}');

            var path = Path.Combine(dir, SeenFileName(host));
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

    /// <summary>
    /// True when the host's direct stamp exists, parses, is at most 30 days old and is not
    /// more than a day in the future (a clock-skewed or hostile stamp never silences the notice).
    /// </summary>
    public static bool IsDirectStampFresh(string dir, string host, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, StampFileName(host))));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("at", out var v) || v.ValueKind != JsonValueKind.String) return false;
            if (!DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                return false;
            var age = now - at;
            return age <= StampMaxAge && -age <= StampMaxFuture;
        }
        catch
        {
            return false;
        }
    }
}
