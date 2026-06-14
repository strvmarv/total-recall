// src/TotalRecall.Server/ProvisionNotice.cs
//
// v3.0.4 — one-time reader/consumer for the Node provisioner's first-run
// marker. The provisioner (scripts/fetch-binary.js) writes a
// `.provisioned.json` file NEXT TO the server executable after the first-run
// binary download completes. Shape (shared contract):
//
//   { "version": string, "sizeBytes": number, "durationMs": number,
//     "completedAtUnixMs": number }
//
// The server reads it once via ReadAndConsume — surfacing a "first-run setup
// complete" notice through session_start — and then DELETES it. The deletion
// IS the one-time mechanism: a second call in the same process (or a later
// session) returns null because the file is gone.
//
// AOT-safe: parsed via the source-generated JsonContext (no reflection-based
// JsonSerializer.Deserialize<T>(string)). Maps onto SetupNoticeDto with
// Event = "provisioned".

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TotalRecall.Server;

/// <summary>
/// Reads and consumes (deletes) the Node provisioner's one-time
/// <c>.provisioned.json</c> marker. Best-effort throughout: any IO/parse
/// failure yields <c>null</c> and never throws; the delete is also
/// best-effort.
/// </summary>
public static class ProvisionNotice
{
    /// <summary>Marker filename written by the Node provisioner next to the executable.</summary>
    public const string MarkerFileName = ".provisioned.json";

    /// <summary>
    /// Reads <c>{binaryDir}/.provisioned.json</c>, returns a populated
    /// <see cref="SetupNoticeDto"/> (Event = "provisioned"), then best-effort
    /// DELETES the file so a subsequent call returns <c>null</c>. Returns
    /// <c>null</c> when the marker is absent, unreadable, or unparseable.
    /// </summary>
    public static SetupNoticeDto? ReadAndConsume(string binaryDir)
    {
        if (string.IsNullOrEmpty(binaryDir)) return null;

        string path;
        try
        {
            path = Path.Combine(binaryDir, MarkerFileName);
        }
        catch
        {
            return null;
        }

        string json;
        try
        {
            if (!File.Exists(path)) return null;
            json = File.ReadAllText(path);
        }
        catch
        {
            // Any IO failure (locked file, permissions, race with delete) → no notice.
            return null;
        }

        ProvisionMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize(json, JsonContext.Default.ProvisionMarker);
        }
        catch
        {
            // Garbage / non-JSON → no notice. Still consume the file below so a
            // malformed marker can't wedge every future session.
            marker = null;
        }

        // Best-effort delete regardless of parse outcome — the file is one-time.
        try { File.Delete(path); } catch { /* best-effort */ }

        if (marker is null) return null;

        return new SetupNoticeDto(
            Event: "provisioned",
            Version: marker.Version ?? "",
            SizeBytes: marker.SizeBytes,
            DurationMs: marker.DurationMs);
    }
}

/// <summary>
/// Wire shape of the Node provisioner's <c>.provisioned.json</c> marker.
/// Property-body style with nullable Version so a marker missing fields
/// deserializes without throwing. Registered in <see cref="JsonContext"/>.
/// </summary>
public sealed record ProvisionMarker
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("completedAtUnixMs")]
    public long CompletedAtUnixMs { get; init; }
}
