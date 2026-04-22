// src/TotalRecall.Server/SessionLifecycle.cs
//
// Plan 4 Task 4.3 — port of src-ts/tools/session-tools.ts runSessionInit,
// scoped to the Infrastructure pieces that exist in .NET as of Plan 4:
// host importers, IStore, and a CompactionLog read seam. Everything
// else (warm sweep, semantic promote, project docs ingest, smoke test,
// regression detection, project detection, config snapshot) is stubbed
// with `TODO(Plan 5+)` markers at each would-be call site.
//
// Wire shape returned by EnsureInitializedAsync is consumed by Task 4.10's
// session_start handler. The shape is registered with JsonContext so the
// source-generated serializer can render it AOT-safely.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server;

/// <summary>
/// Production <see cref="ISessionLifecycle"/>. Composes a list of host
/// importers, an <see cref="IStore"/>, and a small
/// <see cref="ICompactionLogReader"/> seam. Caches the first
/// <see cref="EnsureInitializedAsync"/> result behind an async lock so
/// concurrent callers (notification + first tool call racing) collapse to a
/// single import sweep.
/// </summary>
public sealed class SessionLifecycle : ISessionLifecycle
{
    private readonly IReadOnlyList<IImporter> _importers;
    private readonly IStore _store;
    private readonly ICompactionLogReader _compactionLog;
    private readonly Func<long> _nowMs;
    private readonly string _sessionId;
    private readonly string _storageMode;
    private readonly TotalRecall.Infrastructure.Usage.UsageIndexer? _usageIndexer;
    private readonly ISkillImportService? _skillImportService;
    private readonly int _tokenBudget;
    private readonly int _maxEntries;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private SessionInitResult? _cached;

    public SessionLifecycle(
        IReadOnlyList<IImporter> importers,
        IStore store,
        ICompactionLogReader compactionLog,
        string? sessionId = null,
        Func<long>? nowMs = null,
        TotalRecall.Infrastructure.Usage.UsageIndexer? usageIndexer = null,
        string storageMode = "sqlite",
        ISkillImportService? skillImportService = null,
        TimeSpan? skillImportTimeout = null, // kept for source compat; ignored — import is fire-and-forget
        int tokenBudget = 4000,
        int maxEntries = 50)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(compactionLog);
        _importers = importers;
        _store = store;
        _compactionLog = compactionLog;
        _sessionId = sessionId ?? Guid.NewGuid().ToString();
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _usageIndexer = usageIndexer;
        _storageMode = storageMode;
        _skillImportService = skillImportService;
        _tokenBudget = tokenBudget > 0 ? tokenBudget : 4000;
        _maxEntries = maxEntries > 0 ? maxEntries : 50;
    }

    /// <inheritdoc />
    public bool IsInitialized => _cached is not null;

    /// <inheritdoc />
    public string SessionId => _sessionId;

    /// <inheritdoc />
    public async Task<SessionInitResult> EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            _cached = RunInit();
            return _cached;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private SessionInitResult RunInit()
    {
        // 0. Usage indexer — best-effort, failures never block session_start.
        //    Runs before the existing importer sweep so quota nudges reflect
        //    the latest data. See token-usage-tracking spec §5.4.
        if (_usageIndexer is not null)
        {
            try
            {
                _usageIndexer.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Never propagate — usage tracking must not prevent server boot.
                Console.Error.WriteLine($"total-recall: usage indexer failed: {ex.Message}");
            }
        }

        // 1. Host importer sweep — only invoke importers whose Detect() trips.
        var importSummary = new List<ImportSummaryRow>();
        foreach (var importer in _importers)
        {
            if (!importer.Detect()) continue;

            // TODO(Plan 5+): pass detected project name once project detection
            // lands. The Plan 4 stub passes null which matches the importer's
            // own "global memories" path.
            var memResult = importer.ImportMemories(project: null);
            var kbResult = importer.ImportKnowledge();

            importSummary.Add(new ImportSummaryRow(
                importer.Name,
                memResult.Imported,
                kbResult.Imported,
                SkillsImported: 0,
                SkillsUpdated: 0,
                SkillsUnchanged: 0,
                SkillsOrphaned: 0,
                SkillsErrors: Array.Empty<string>()));
        }

        // 1b+1c. Skill import sweep + listing.
        //
        // Import push (1b) — detached from the critical path. Fire and forget so
        // session_start is never delayed by a slow or unreachable cortex server.
        // CancellationToken.None is intentional: the HTTP call must complete even
        // if the MCP session token is cancelled.
        //
        // Listing (1c) — still synchronous so the skills block in the session
        // context is populated on the first call. Best-effort; failures produce
        // an empty block, never an exception.
        string skillsBlock = string.Empty;
        if (_skillImportService is not null)
        {
            // 1b. Fire-and-forget import push.
            // CancellationToken.None is intentional — the push must complete even if
            // the MCP session token is cancelled. The try/catch around ImportAsync
            // guards against synchronous throws (e.g. configuration errors) so they
            // don't surface through the unobserved-task path.
            Task importTask;
            try
            {
                importTask = _skillImportService.ImportAsync(Environment.CurrentDirectory, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: background skill import failed: {ex.Message}");
                importTask = Task.CompletedTask;
            }
            _ = importTask.ContinueWith(
                t => Console.Error.WriteLine($"total-recall: background skill import failed: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // The import runs in the background; session_start always reports zero
            // skill counts on this call. The next PeriodicSync or explicit
            // skill_import_host tool invocation will reflect real numbers.

            // 1b-extra. Scan extra_dirs locally — always, independent of cortex availability.
            IReadOnlyList<ImportedSkill> localExtraSkills = Array.Empty<ImportedSkill>();
            try
            {
                var localScan = _skillImportService
                    .ScanExtraDirsAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                localExtraSkills = localScan.Skills;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: extra_dirs scan failed: {ex.Message}");
            }

            // 1c. Cortex list — best-effort; local extra_dirs skills fill the gap when
            // cortex is unavailable or returns nothing. Both merged by BuildSkillsBlock.
            SkillListResponseDto? cortexList = null;
            try
            {
                cortexList = _skillImportService
                    .ListVisibleAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: skill listing failed: {ex.Message}");
            }

            skillsBlock = BuildSkillsBlock(cortexList, localExtraSkills);
        }

        // TODO(Plan 5+): project docs auto-ingest — ProjectDocsImporter exists
        // in Infrastructure but Plan 4 explicitly excludes it from session init.

        // TODO(Plan 5+): smoke test — eval/smoke-test.ts not yet ported.

        // TODO(Plan 5+): semantic warm→hot promotion driven by detected project.
        // Plan 4 leaves warmPromotedIds empty.
        var warmPromotedIds = Array.Empty<string>();
        var warmPromoted = 0;

        // 2. Warm sweep — synchronous, runs before hot entry listing so the
        // reported count reflects post-sweep state. Eliminates the Task.Run
        // thread-race against PeriodicSync on the shared SQLite connection.
        RunWarmSweep();

        // 3. Hot entries listing — used for both context and hot count.
        var hotEntries = _store.List(Tier.Hot, ContentType.Memory);

        // 4. Tier summary.
        var tierSummary = new TierSummary(
            Hot: _store.Count(Tier.Hot, ContentType.Memory),
            Warm: _store.Count(Tier.Warm, ContentType.Memory)
                + _store.Count(Tier.Warm, ContentType.Knowledge),
            Cold: _store.Count(Tier.Cold, ContentType.Memory)
                + _store.Count(Tier.Cold, ContentType.Knowledge),
            Kb: _store.Count(Tier.Hot, ContentType.Knowledge)
                + _store.Count(Tier.Warm, ContentType.Knowledge)
                + _store.Count(Tier.Cold, ContentType.Knowledge),
            Collections: _store.CountKnowledgeCollections());

        // 5. Context string assembly — matches TS session-tools.ts:260-264.
        var (baseContext, hotContextTruncated) = BuildContext(hotEntries, _tokenBudget);

        // Append skills block after a blank-line separator when both parts are non-empty.
        string context;
        if (baseContext.Length > 0 && skillsBlock.Length > 0)
            context = baseContext + "\n\n" + skillsBlock;
        else if (skillsBlock.Length > 0)
            context = skillsBlock;
        else
            context = baseContext;

        // 6. Hints.
        var hints = GenerateHints(_store, warmPromotedIds);

        // 7. Last session age (humanized).
        var lastAgeMs = _compactionLog.GetLastTimestampExcludingReason("warm_sweep_decay");
        var lastSessionAge = FormatLastSessionAge(lastAgeMs, _nowMs());

        // TODO(Plan 5+): regression detection — checkRegressions from
        // src-ts/eval/regression.ts not yet ported. Leave alerts null.

        // TODO(Plan 5+): config snapshot creation — createConfigSnapshot
        // from src-ts/config.ts not yet ported. Snapshot id stays null.

        return new SessionInitResult(
            SessionId: _sessionId,
            Project: null, // TODO(Plan 5+): detectProject() not ported
            ImportSummary: importSummary,
            WarmSweep: null, // TODO(Plan 5+): warm sweep not ported
            WarmPromoted: warmPromoted,
            ProjectDocs: null, // TODO(Plan 5+): project docs auto-ingest not in scope
            HotEntryCount: hotEntries.Count,
            Context: context,
            TierSummary: tierSummary,
            Hints: hints,
            LastSessionAge: lastSessionAge,
            SmokeTest: null, // TODO(Plan 5+): smoke test not ported
            RegressionAlerts: null, // TODO(Plan 5+): regression detection not ported
            Storage: _storageMode,
            HotContextTruncated: hotContextTruncated);
    }

    private void RunWarmSweep()
    {
        try
        {
            var count = _store.Count(Tier.Hot, ContentType.Memory);
            if (count <= _maxEntries) return;

            var excess = count - _maxEntries;
            var toEvict = _store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { OrderBy = "decay_score ASC", Limit = excess });

            foreach (var entry in toEvict)
            {
                try { _store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, entry.Id); }
                catch (InvalidOperationException) { } // entry deleted by concurrent write — skip
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"total-recall: warm sweep failed: {ex.Message}");
        }
    }

    // -------- helpers (internal so tests can call them directly) --------

    /// <summary>
    /// Builds the <c>## Available Skills</c> context block from a list response.
    /// Returns an empty string when there are no skills.
    /// </summary>
    public static string BuildSkillsBlock(
        TotalRecall.Infrastructure.Skills.SkillListResponseDto? cortexList,
        IReadOnlyList<TotalRecall.Infrastructure.Skills.ImportedSkill>? localExtraSkills = null)
    {
        var cortexItems = cortexList?.Items
            ?? Array.Empty<TotalRecall.Infrastructure.Skills.SkillDto>();

        // Local extra_dirs skills not already present in the cortex list (dedup by name).
        var cortexNames = new HashSet<string>(
            cortexItems.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var localOnly = (localExtraSkills
            ?? Array.Empty<TotalRecall.Infrastructure.Skills.ImportedSkill>())
            .Where(s => !cortexNames.Contains(s.Name))
            .ToList();

        if (cortexItems.Count == 0 && localOnly.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Available Skills");
        sb.AppendLine("Use skill_get(name: \"...\") to retrieve full content, or skill_search(query: \"...\") to find others.");
        sb.AppendLine();

        foreach (var skill in cortexItems)
        {
            var desc = string.IsNullOrWhiteSpace(skill.Description)
                ? "(no description)"
                : skill.Description.Trim();
            sb.Append("- ").Append(skill.Name).Append(": ").AppendLine(desc);
        }

        foreach (var skill in localOnly)
        {
            var desc = string.IsNullOrWhiteSpace(skill.Description)
                ? "(no description)"
                : skill.Description.Trim();
            sb.Append("- ").Append(skill.Name).Append(": ").AppendLine(desc);
        }

        // Remove trailing newline from the last skill line.
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
        {
            sb.Remove(sb.Length - 1, 1);
            if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                sb.Remove(sb.Length - 1, 1);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the hot-tier context block fed back to the host LLM. Entries are
    /// sorted by <see cref="Entry.DecayScore"/> descending and then rendered as
    /// <c>"- {content}{tags_suffix}"</c> with
    /// <c>tags_suffix</c> = <c>" [tag1, tag2]"</c> when tags are present, empty
    /// otherwise. Lines joined with '\n'. Enforces a token budget (1 token ≈ 4 chars).
    /// Returns a tuple of the context string and a boolean indicating whether the
    /// output was truncated due to the budget. Mirrors TS lines 260-264.
    /// </summary>
    public static (string Context, bool Truncated) BuildContext(
        IReadOnlyList<Entry> hotEntries,
        int tokenBudget = 4000)
    {
        if (hotEntries.Count == 0) return (string.Empty, false);

        var maxChars = tokenBudget * 4;
        var sorted = hotEntries.OrderByDescending(e => e.DecayScore);
        var result = new StringBuilder();
        var charsUsed = 0;
        var first = true;
        var truncated = false;

        foreach (var e in sorted)
        {
            var line = BuildContextLine(e);
            var needed = (first ? 0 : 1) + line.Length; // '\n' separator between entries

            if (charsUsed + needed > maxChars)
            {
                truncated = true;
                break;
            }

            if (!first) result.Append('\n');
            result.Append(line);
            charsUsed += needed;
            first = false;
        }

        return (result.ToString(), truncated);
    }

    private static string BuildContextLine(Entry e)
    {
        var sb = new StringBuilder("- ");
        sb.Append(e.Content);
        // Entry.Tags is FSharpList<string>; iterate as IEnumerable<string>.
        var tags = (IEnumerable<string>)e.Tags;
        var tagList = tags as IList<string> ?? tags.ToList();
        if (tagList.Count > 0)
        {
            sb.Append(" [");
            sb.Append(string.Join(", ", tagList));
            sb.Append(']');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Truncate <paramref name="content"/> to <paramref name="maxLen"/>
    /// characters, appending <c>"..."</c> when truncation occurs. Mirrors TS
    /// <c>truncateHint</c>.
    /// </summary>
    public static string TruncateHint(string content, int maxLen = 120)
    {
        if (content.Length <= maxLen) return content;
        return content.Substring(0, maxLen) + "...";
    }

    /// <summary>
    /// Generates the actionable-hints list. Three priorities, capped at 5.
    /// Mirrors TS <c>generateHints</c> in session-tools.ts:27-74.
    /// </summary>
    public static IReadOnlyList<string> GenerateHints(
        IStore store,
        IReadOnlyList<string> warmPromotedIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hints = new List<string>();

        // Priority 1: corrections + preferences (max 2).
        var corrections = store.ListByMetadata(
            Tier.Warm,
            ContentType.Memory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["entry_type"] = "correction" },
            new ListEntriesOpts { OrderBy = "access_count DESC", Limit = 2 });

        var preferences = store.ListByMetadata(
            Tier.Warm,
            ContentType.Memory,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["entry_type"] = "preference" },
            new ListEntriesOpts { OrderBy = "access_count DESC", Limit = 2 });

        var p1 = corrections.Concat(preferences)
            .OrderByDescending(e => e.AccessCount)
            .ThenBy(e => e.CreatedAt)
            .Take(2);

        foreach (var entry in p1)
        {
            if (seen.Add(entry.Id))
                hints.Add(TruncateHint(entry.Content));
        }

        // Priority 2: frequently accessed (access_count >= 3, max 2).
        var frequent = store.List(
            Tier.Warm,
            ContentType.Memory,
            new ListEntriesOpts { OrderBy = "access_count DESC", Limit = 10 });

        var taken = 0;
        foreach (var entry in frequent)
        {
            if (taken >= 2) break;
            if (entry.AccessCount < 3) continue;
            if (seen.Contains(entry.Id)) continue;
            seen.Add(entry.Id);
            hints.Add(TruncateHint(entry.Content));
            taken++;
        }

        // Priority 3: recently warm-promoted (max 1). Plan 4 passes [].
        foreach (var id in warmPromotedIds.Take(1))
        {
            if (seen.Contains(id)) continue;
            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            if (entry is null) continue;
            seen.Add(entry.Id);
            hints.Add(TruncateHint(entry.Content));
        }

        if (hints.Count > 5) hints.RemoveRange(5, hints.Count - 5);
        return hints;
    }

    /// <summary>
    /// Format a last-session-age string from a timestamp + now (both unix ms).
    /// Mirrors TS <c>getLastSessionAge</c> in session-tools.ts:76-99.
    /// </summary>
    public static string? FormatLastSessionAge(long? lastTimestampMs, long nowMs)
    {
        if (lastTimestampMs is null || lastTimestampMs.Value <= 0) return null;
        var diffMs = nowMs - lastTimestampMs.Value;
        if (diffMs < 0) diffMs = 0;
        var minutes = (long)Math.Floor(diffMs / 60000.0);
        var hours = (long)Math.Floor(diffMs / 3600000.0);
        var days = (long)Math.Floor(diffMs / 86400000.0);
        var weeks = days / 7;

        if (minutes == 0) return "just now";
        if (minutes == 1) return "1 minute ago";
        if (minutes < 60) return minutes.ToString(CultureInfo.InvariantCulture) + " minutes ago";
        if (hours == 1) return "1 hour ago";
        if (hours < 24) return hours.ToString(CultureInfo.InvariantCulture) + " hours ago";
        if (days == 1) return "1 day ago";
        if (days < 7) return days.ToString(CultureInfo.InvariantCulture) + " days ago";
        if (weeks == 1) return "1 week ago";
        return weeks.ToString(CultureInfo.InvariantCulture) + " weeks ago";
    }
}

// ---------- result records ----------

/// <summary>
/// Aggregate result of <see cref="ISessionLifecycle.EnsureInitializedAsync"/>.
/// Plan 4 leaves several fields stubbed; see the field-level remarks for the
/// <c>TODO(Plan 5+)</c> markers.
/// </summary>
public sealed record SessionInitResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("project")] string? Project,
    [property: JsonPropertyName("importSummary")] IReadOnlyList<ImportSummaryRow> ImportSummary,
    [property: JsonPropertyName("warmSweep")] WarmSweepResult? WarmSweep,
    [property: JsonPropertyName("warmPromoted")] int WarmPromoted,
    [property: JsonPropertyName("projectDocs")] ProjectDocsResult? ProjectDocs,
    [property: JsonPropertyName("hotEntryCount")] int HotEntryCount,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("tierSummary")] TierSummary TierSummary,
    [property: JsonPropertyName("hints")] IReadOnlyList<string> Hints,
    [property: JsonPropertyName("lastSessionAge")] string? LastSessionAge,
    [property: JsonPropertyName("smokeTest")] SmokeTestResult? SmokeTest,
    [property: JsonPropertyName("regressionAlerts")] IReadOnlyList<RegressionAlert>? RegressionAlerts,
    [property: JsonPropertyName("storage")] string Storage,
    [property: JsonPropertyName("hotContextTruncated")] bool HotContextTruncated);

/// <summary>One row in the host-importer summary. Skill fields default to
/// zero / empty for legacy memory-only importers; the skill import sweep
/// merges by tool name and populates them in place (see
/// <see cref="SessionLifecycle"/>).</summary>
public sealed record ImportSummaryRow(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("memoriesImported")] int MemoriesImported,
    [property: JsonPropertyName("knowledgeImported")] int KnowledgeImported,
    [property: JsonPropertyName("skillsImported")] int SkillsImported,
    [property: JsonPropertyName("skillsUpdated")] int SkillsUpdated,
    [property: JsonPropertyName("skillsUnchanged")] int SkillsUnchanged,
    [property: JsonPropertyName("skillsOrphaned")] int SkillsOrphaned,
    [property: JsonPropertyName("skillsErrors")] IReadOnlyList<string> SkillsErrors);

/// <summary>Tier-level row counts for the session-init summary block.</summary>
public sealed record TierSummary(
    [property: JsonPropertyName("hot")] int Hot,
    [property: JsonPropertyName("warm")] int Warm,
    [property: JsonPropertyName("cold")] int Cold,
    [property: JsonPropertyName("kb")] int Kb,
    [property: JsonPropertyName("collections")] int Collections);

// TODO(Plan 5+): populate when warm sweep lands.
public sealed record WarmSweepResult(
    [property: JsonPropertyName("demoted")] int Demoted);

// TODO(Plan 5+): populate when project docs auto-ingest lands.
public sealed record ProjectDocsResult(
    [property: JsonPropertyName("filesIngested")] int FilesIngested,
    [property: JsonPropertyName("totalChunks")] int TotalChunks);

// TODO(Plan 5+): populate when smoke test lands.
public sealed record SmokeTestResult(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("notes")] string? Notes);

// TODO(Plan 5+): populate when regression detection lands.
public sealed record RegressionAlert(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("message")] string Message);
