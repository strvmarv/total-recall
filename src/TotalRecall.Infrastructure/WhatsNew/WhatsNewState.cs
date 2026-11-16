// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewState.cs
//
// Per-host state files for the what's-new notice, under <dataDir>/whats-new/:
//   seen-<host>.json    {"version":"<bare version>","availableSeen":{"stable":"<v>","pre":"<v>"}}
//                       last version this host was told about, and per channel the newest
//                       available release it was told about (each key omitted when unset)
//   direct-<host>.json  {"at":"<ISO-8601 UTC>"}        stamp written by the host's own notice path
// A host reaches a file name only through HostIds.Normalize. Reads are fail-safe
// (any error is "no state"); writes are atomic and swallow IO errors.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;

namespace TotalRecall.Infrastructure.WhatsNew;

/// <summary>Contents of a seen file; <see cref="AvailableSeen"/> only ever holds "stable" and "pre".</summary>
public sealed record SeenState(string? Version, IReadOnlyDictionary<string, string> AvailableSeen)
{
    public static SeenState Empty { get; } = new(null, new Dictionary<string, string>());
}

public static class WhatsNewState
{
    private static readonly string[] ChannelOrder = { UpdateChannel.Stable, UpdateChannel.Pre };
    private static readonly TimeSpan StampMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan StampMaxFuture = TimeSpan.FromDays(1);

    public static string DefaultDir() => Path.Combine(ConfigLoader.GetDataDir(), "whats-new");

    public static string SeenFileName(string host) => "seen-" + HostIds.Normalize(host) + ".json";

    public static string StampFileName(string host) => "direct-" + HostIds.Normalize(host) + ".json";

    public static string? ReadSeenVersion(string dir, string host) => ReadSeen(dir, host).Version;

    /// <summary>
    /// The host's seen file: the last version it was told about and, per channel, the newest
    /// available release it was told about. Never null; any error gives <see cref="SeenState.Empty"/>.
    /// </summary>
    public static SeenState ReadSeen(string dir, string host)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, SeenFileName(host))));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SeenState.Empty;

            string? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            var available = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("availableSeen", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                foreach (var channel in ChannelOrder)
                {
                    if (a.TryGetProperty(channel, out var cv) && cv.ValueKind == JsonValueKind.String
                        && cv.GetString() is { } value)
                    {
                        available[channel] = value;
                    }
                }
            }
            return new SeenState(version, available);
        }
        catch
        {
            return SeenState.Empty;
        }
    }

    /// <summary>Records the version the host was told about, keeping any availableSeen marks.</summary>
    public static void WriteSeenVersion(string dir, string host, string version)
    {
        if (version is null) return;
        var seen = ReadSeen(dir, host);
        WriteSeen(dir, host, version, seen.AvailableSeen);
    }

    /// <summary>
    /// Records the newest release of <paramref name="channel"/> the host was told about, keeping the
    /// seen version and the other channel's mark. An unknown channel writes nothing.
    /// </summary>
    public static void WriteAvailableSeen(string dir, string host, string channel, string version)
    {
        if (!UpdateChannel.IsKnown(channel)) return;
        var seen = ReadSeen(dir, host);
        var available = new Dictionary<string, string>(seen.AvailableSeen, StringComparer.Ordinal)
        {
            [channel] = version,
        };
        WriteSeen(dir, host, seen.Version, available);
    }

    private static void WriteSeen(string dir, string host, string? version,
        IReadOnlyDictionary<string, string> availableSeen)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append('{');
            var first = true;
            if (version is not null)
            {
                JsonWriter.AppendString(sb, "version"); sb.Append(':');
                JsonWriter.AppendString(sb, version);
                first = false;
            }
            if (availableSeen.Count > 0)
            {
                if (!first) sb.Append(',');
                JsonWriter.AppendString(sb, "availableSeen"); sb.Append(":{");
                var innerFirst = true;
                foreach (var channel in ChannelOrder)
                {
                    if (!availableSeen.TryGetValue(channel, out var value)) continue;
                    if (!innerFirst) sb.Append(',');
                    JsonWriter.AppendString(sb, channel); sb.Append(':');
                    JsonWriter.AppendString(sb, value);
                    innerFirst = false;
                }
                sb.Append('}');
            }
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
