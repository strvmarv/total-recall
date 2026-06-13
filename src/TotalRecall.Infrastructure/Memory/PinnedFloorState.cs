// src/TotalRecall.Infrastructure/Memory/PinnedFloorState.cs
//
// Per-session throttle state for the pinned-directive floor. Stored as a small
// JSON file under <dataDir>/floor-state/<sanitized-session>.json. Reads are
// fail-safe: any error yields a fresh, unseeded state (never throws).

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;

namespace TotalRecall.Infrastructure.Memory;

public sealed record FloorState(
    string SessionId,
    int TurnCount,
    int LastInjectedTurn,
    long LastInjectedBytes,
    bool Seeded);

public static class PinnedFloorState
{
    public static string DefaultStateDir() =>
        Path.Combine(ConfigLoader.GetDataDir(), "floor-state");

    public static string FileName(string sessionId)
    {
        var sb = new StringBuilder(sessionId.Length + 5);
        foreach (var ch in sessionId)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        if (sb.Length == 0) sb.Append("session");
        sb.Append(".json");
        return sb.ToString();
    }

    public static FloorState Load(string stateDir, string sessionId)
    {
        try
        {
            var path = Path.Combine(stateDir, FileName(sessionId));
            if (!File.Exists(path)) return Fresh(sessionId);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            return new FloorState(
                SessionId: sessionId,
                TurnCount: GetInt(r, "turnCount"),
                LastInjectedTurn: GetInt(r, "lastInjectedTurn"),
                LastInjectedBytes: GetLong(r, "lastInjectedBytes"),
                Seeded: r.TryGetProperty("seeded", out var s) && s.ValueKind == JsonValueKind.True);
        }
        catch
        {
            return Fresh(sessionId);
        }
    }

    public static void Save(string stateDir, FloorState state)
    {
        Directory.CreateDirectory(stateDir);
        var sb = new StringBuilder();
        sb.Append('{');
        JsonWriter.AppendString(sb, "sessionId"); sb.Append(':');
        JsonWriter.AppendString(sb, state.SessionId); sb.Append(',');
        JsonWriter.AppendString(sb, "turnCount"); sb.Append(':');
        JsonWriter.AppendNumber(sb, state.TurnCount); sb.Append(',');
        JsonWriter.AppendString(sb, "lastInjectedTurn"); sb.Append(':');
        JsonWriter.AppendNumber(sb, state.LastInjectedTurn); sb.Append(',');
        JsonWriter.AppendString(sb, "lastInjectedBytes"); sb.Append(':');
        JsonWriter.AppendNumber(sb, state.LastInjectedBytes); sb.Append(',');
        JsonWriter.AppendString(sb, "seeded"); sb.Append(':');
        JsonWriter.AppendBool(sb, state.Seeded);
        sb.Append('}');

        var path = Path.Combine(stateDir, FileName(state.SessionId));
        File.WriteAllText(path, sb.ToString());
    }

    public static void Prune(string stateDir, double maxAgeDays, DateTimeOffset nowUtc)
    {
        try
        {
            if (!Directory.Exists(stateDir)) return;
            var cutoff = nowUtc.UtcDateTime.AddDays(-maxAgeDays);
            foreach (var file in Directory.EnumerateFiles(stateDir, "*.json"))
            {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                catch { }
            }
        }
        catch { }
    }

    private static FloorState Fresh(string sessionId) => new(sessionId, 0, 0, 0L, false);

    private static int GetInt(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;
}
