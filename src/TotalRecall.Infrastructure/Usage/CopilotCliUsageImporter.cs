// src/TotalRecall.Infrastructure/Usage/CopilotCliUsageImporter.cs
//
// Parses Copilot CLI's session-state events.jsonl files into UsageEvent.
// Layout: ~/.copilot/session-state/<session-uuid>/events.jsonl
//
// Schema verified empirically on copilot@1.0.22 (2026-04-09). Only
// assistant.message events carry token data (`data.outputTokens`).
// Model name is NOT on the message itself — it's on the most recent
// tool.execution_complete event in the same session, so we maintain a
// "last known model" running variable.
//
// Context tracking: session.start.data.context initializes project
// attribution (cwd, gitRoot, branch, repository, headCommit). Mid-session
// session.context_changed events update the running context; subsequent
// assistant.message events attribute to the new context.
//
// Copilot CLI provides NONE of the Anthropic input/cache token fields.
// Every UsageEvent emitted here leaves InputTokens, CacheCreation*, CacheRead,
// ServiceTier, and ServerToolUseJson as null. This is the "unified schema,
// optional fields" decision from spec Q3=B.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Infrastructure.Usage;

public sealed class CopilotCliUsageImporter : IUsageImporter
{
    private readonly string _sessionStateDir;

    public CopilotCliUsageImporter(string? copilotHome = null)
    {
        var home = copilotHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");
        _sessionStateDir = Path.Combine(home, "session-state");
    }

    public string HostName => "copilot-cli";

    public bool Detect() => Directory.Exists(_sessionStateDir);

    public async IAsyncEnumerable<UsageEvent> ScanAsync(
        long sinceMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Detect()) yield break;

        foreach (var sessionDir in Directory.EnumerateDirectories(_sessionStateDir))
        {
            ct.ThrowIfCancellationRequested();
            var eventsPath = Path.Combine(sessionDir, "events.jsonl");
            if (!File.Exists(eventsPath)) continue;

            var mtimeMs = new DateTimeOffset(
                File.GetLastWriteTimeUtc(eventsPath), TimeSpan.Zero).ToUnixTimeMilliseconds();
            if (mtimeMs < sinceMs) continue;

            await foreach (var evt in ParseEventsAsync(eventsPath, sinceMs, ct))
                yield return evt;
        }
    }

    private static async IAsyncEnumerable<UsageEvent> ParseEventsAsync(
        string eventsPath,
        long sinceMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sessionId = Path.GetFileName(Path.GetDirectoryName(eventsPath)!);
        int turnIndex = 0;

        // Running project context — updated by session.start and session.context_changed
        string? projectPath = null, projectRepo = null, projectBranch = null, projectCommit = null;

        // Running model attribution — updated by tool.execution_complete
        string? lastKnownModel = null;

        await using var stream = File.OpenRead(eventsPath);
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
                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                    continue;
                var type = typeEl.GetString();
                if (type is null) continue;

                var data = root.TryGetProperty("data", out var d) ? d : default;

                switch (type)
                {
                    case "session.start":
                        if (data.ValueKind == JsonValueKind.Object
                            && data.TryGetProperty("context", out var ctx)
                            && ctx.ValueKind == JsonValueKind.Object)
                        {
                            projectPath   = GetStringOrNull(ctx, "cwd") ?? projectPath;
                            projectRepo   = GetStringOrNull(ctx, "repository") ?? projectRepo;
                            projectBranch = GetStringOrNull(ctx, "branch") ?? projectBranch;
                            projectCommit = GetStringOrNull(ctx, "headCommit") ?? projectCommit;
                        }
                        break;

                    case "session.context_changed":
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            projectPath   = GetStringOrNull(data, "cwd") ?? projectPath;
                            projectRepo   = GetStringOrNull(data, "repository") ?? projectRepo;
                            projectBranch = GetStringOrNull(data, "branch") ?? projectBranch;
                            projectCommit = GetStringOrNull(data, "headCommit") ?? projectCommit;
                        }
                        break;

                    case "tool.execution_complete":
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            var m = GetStringOrNull(data, "model");
                            if (!string.IsNullOrEmpty(m)) lastKnownModel = m;
                        }
                        break;

                    case "assistant.message":
                        var tsMs = ParseTimestampMs(root);
                        if (tsMs <= sinceMs) continue;

                        var hostEventId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString() ?? Guid.NewGuid().ToString()
                            : Guid.NewGuid().ToString();

                        int? outputTokens = null;
                        string? interactionId = null;
                        string? requestId = null;
                        if (data.ValueKind == JsonValueKind.Object)
                        {
                            outputTokens = GetIntOrNull(data, "outputTokens");
                            interactionId = GetStringOrNull(data, "interactionId");
                            requestId = GetStringOrNull(data, "requestId");
                        }

                        yield return new UsageEvent(
                            Host: "copilot-cli",
                            HostEventId: hostEventId,
                            SessionId: sessionId,
                            TimestampMs: tsMs,
                            TurnIndex: turnIndex++,
                            Model: lastKnownModel,
                            ProjectPath: projectPath,
                            ProjectRepo: projectRepo,
                            ProjectBranch: projectBranch,
                            ProjectCommit: projectCommit,
                            InteractionId: interactionId,
                            InputTokens: null,
                            CacheCreation5m: null,
                            CacheCreation1h: null,
                            CacheRead: null,
                            OutputTokens: outputTokens,
                            ServiceTier: null,
                            ServerToolUseJson: null,
                            HostRequestId: requestId);
                        break;
                }
            }
        }
    }

    // --- helpers ---------------------------------------------------------

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
}
