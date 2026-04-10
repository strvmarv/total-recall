// src/TotalRecall.Infrastructure/Usage/ClaudeCodeUsageImporter.cs
//
// Parses Claude Code transcript files (~/.claude/projects/<encoded-cwd>/
// <session-uuid>.jsonl) into host-neutral UsageEvent records. Only
// records with a message.usage object are emitted — those are the
// "assistant turns" that count for token accounting. Malformed JSON
// lines are skipped silently to match Claude Code's own resilience
// policy (partial writes during a crash).
//
// Fidelity: Claude Code provides the full Anthropic usage object so
// every token field on UsageEvent is populated. ProjectRepo/Branch/Commit
// are left null — Claude Code only gives us the cwd via the encoded
// directory name.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Infrastructure.Usage;

public sealed class ClaudeCodeUsageImporter : IUsageImporter
{
    private const string HostConst = "claude-code";

    private readonly string _projectsDir;

    public ClaudeCodeUsageImporter(string? projectsDir = null)
    {
        _projectsDir = projectsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");
    }

    public string HostName => HostConst;

    public bool Detect() => Directory.Exists(_projectsDir);

    public async IAsyncEnumerable<UsageEvent> ScanAsync(
        long sinceMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Detect()) yield break;

        foreach (var projectDir in Directory.EnumerateDirectories(_projectsDir))
        {
            ct.ThrowIfCancellationRequested();
            var cwd = DecodeProjectDirName(Path.GetFileName(projectDir));

            foreach (var jsonlPath in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                // Skip files whose mtime is strictly older than the watermark.
                // This saves opening files that can't possibly have new events.
                var mtimeMs = new DateTimeOffset(
                    File.GetLastWriteTimeUtc(jsonlPath), TimeSpan.Zero).ToUnixTimeMilliseconds();
                if (mtimeMs < sinceMs) continue;

                await foreach (var evt in ParseTranscriptAsync(jsonlPath, cwd, sinceMs, ct))
                    yield return evt;
            }
        }
    }

    private static async IAsyncEnumerable<UsageEvent> ParseTranscriptAsync(
        string jsonlPath,
        string cwd,
        long sinceMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sessionId = Path.GetFileNameWithoutExtension(jsonlPath);
        int turnIndex = 0;

        await using var stream = File.OpenRead(jsonlPath);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("usage", out var usage)) continue;
                if (usage.ValueKind != JsonValueKind.Object) continue;

                var tsMs = ParseTimestampMs(root);
                if (tsMs <= sinceMs) continue;

                var hostEventId = root.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString() ?? Guid.NewGuid().ToString()
                    : Guid.NewGuid().ToString();

                yield return new UsageEvent(
                    Host: HostConst,
                    HostEventId: hostEventId,
                    SessionId: sessionId,
                    TimestampMs: tsMs,
                    TurnIndex: turnIndex++,
                    Model: GetStringOrNull(msg, "model"),
                    ProjectPath: cwd,
                    ProjectRepo: null,
                    ProjectBranch: null,
                    ProjectCommit: null,
                    InteractionId: null,
                    InputTokens: GetIntOrNull(usage, "input_tokens"),
                    CacheCreation5m: GetCacheCreation(usage, "ephemeral_5m_input_tokens"),
                    CacheCreation1h: GetCacheCreation(usage, "ephemeral_1h_input_tokens"),
                    CacheRead: GetIntOrNull(usage, "cache_read_input_tokens"),
                    OutputTokens: GetIntOrNull(usage, "output_tokens"),
                    ServiceTier: GetStringOrNull(usage, "service_tier"),
                    ServerToolUseJson: GetRawJsonOrNull(usage, "server_tool_use"),
                    HostRequestId: null);
            }
        }
    }

    // --- helpers ---------------------------------------------------------

    /// <summary>
    /// Claude Code encodes cwd paths into directory names by replacing
    /// forward-slashes with hyphens. A double-hyphen ("--") is the escape
    /// for a literal hyphen in the original path. Reverses that.
    /// </summary>
    /// <remarks>
    /// Edge case: a triple-hyphen sequence ("---") is ambiguous — both the
    /// real path "/-foo" and "-/foo" encode to "---foo", and this decoder
    /// resolves in favor of "-/foo" (sentinel replacement produces "-/").
    /// Practically irrelevant on real filesystems (no modern cwd starts
    /// with "/-..."), but documented here so any future debugging over
    /// oddly-named paths has a clear explanation.
    /// </remarks>
    internal static string DecodeProjectDirName(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return encoded;

        // Walk the string replacing `--` with `\x01` (temporary sentinel),
        // then single `-` with `/`, then sentinel back to `-`.
        const char SENTINEL = '\x01';
        var step1 = encoded.Replace("--", SENTINEL.ToString());
        var step2 = step1.Replace('-', '/');
        var step3 = step2.Replace(SENTINEL, '-');
        return step3;
    }

    private static long ParseTimestampMs(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var s = t.GetString();
            if (!string.IsNullOrEmpty(s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dto))
            {
                return dto.ToUnixTimeMilliseconds();
            }
        }
        return 0;
    }

    private static int? GetIntOrNull(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return null;
        // NOTE: TryGetInt32 returns false (→ null) for values exceeding
        // Int32.MaxValue (~2.15B). Realistic single-turn token counts are
        // far below this today. If cache_creation aggregates ever grow
        // past the limit, switch UsageEvent.*Tokens to long? and use
        // TryGetInt64 here. Silent-truncation is preferred over throwing
        // to keep the parser robust against unexpected schema changes.
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            _ => null,
        };
    }

    private static string? GetStringOrNull(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// cache_creation lives in a nested object: usage.cache_creation.{ephemeral_5m,ephemeral_1h}_input_tokens.
    /// Returns null if the nested object is absent or the specific field is missing.
    /// </summary>
    private static int? GetCacheCreation(JsonElement usage, string fieldName)
    {
        if (!usage.TryGetProperty("cache_creation", out var cc)) return null;
        if (cc.ValueKind != JsonValueKind.Object) return null;
        return GetIntOrNull(cc, fieldName);
    }

    private static string? GetRawJsonOrNull(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null || v.ValueKind == JsonValueKind.Undefined) return null;
        return v.GetRawText();
    }
}
