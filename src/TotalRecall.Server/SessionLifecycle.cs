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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Memory;
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
    private readonly TotalRecall.Infrastructure.Usage.UsageQueryService? _usageQuery;
    private readonly ISkillImportService? _skillImportService;
    private readonly IEmbedder? _embedder;
    private readonly int _tokenBudget;
    private readonly int _maxEntries;
    private readonly int _autoDemoteMinInjections;
    private readonly double _taskWeight;
    private readonly Func<long, (int Count, double AvgLatencyMs)>? _retrievalStatsSince;
    private readonly Func<(long Hits, long Misses, long TokensSaved)>? _cacheStats;

    /// <summary>Phase 3 idea 2a — advisory thresholds for RefreshAsync recommendations.</summary>
    private const double SessionRefreshThresholdMinutes = 30;
    private const int RetrievalExtractThreshold = 8;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private SessionInitResult? _cached;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.MinValue;

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
        int maxEntries = 50,
        TotalRecall.Infrastructure.Usage.UsageQueryService? usageQuery = null,
        IEmbedder? embedder = null,
        int autoDemoteMinInjections = 10,
        double taskWeight = 0.0,
        Func<long, (int Count, double AvgLatencyMs)>? retrievalStatsSince = null,
        Func<(long Hits, long Misses, long TokensSaved)>? cacheStats = null)
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
        _usageQuery = usageQuery;
        _storageMode = storageMode;
        _skillImportService = skillImportService;
        _embedder = embedder;
        _tokenBudget = tokenBudget > 0 ? tokenBudget : 4000;
        _maxEntries = maxEntries > 0 ? maxEntries : 50;
        _autoDemoteMinInjections = autoDemoteMinInjections > 0 ? autoDemoteMinInjections : 10;
        _taskWeight = taskWeight >= 0 && taskWeight <= 1 ? taskWeight : 0.0;
        _retrievalStatsSince = retrievalStatsSince;
        _cacheStats = cacheStats;
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
            _sessionStartedAt = DateTimeOffset.UtcNow;
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

        // 3a. Pinned entries — always injected verbatim, ahead of the hot tier
        // (spec 2026-06-09). The hot tier gets whatever budget remains.
        var pinnedMemories = _store.List(Tier.Pinned, ContentType.Memory);
        var pinnedKnowledge = _store.List(Tier.Pinned, ContentType.Knowledge);
        var (pinnedBlock, pinnedIds) = PinnedBlockRenderer.Render(pinnedMemories, pinnedKnowledge);
        var pinnedTokens = pinnedBlock.Length > 0 ? HeuristicEstimateTokens(pinnedBlock) : 0;

        // 4. Tier summary.
        var tierSummary = new TierSummary(
            Hot: _store.Count(Tier.Hot, ContentType.Memory),
            Warm: _store.Count(Tier.Warm, ContentType.Memory)
                + _store.Count(Tier.Warm, ContentType.Knowledge),
            Cold: _store.Count(Tier.Cold, ContentType.Memory)
                + _store.Count(Tier.Cold, ContentType.Knowledge),
            Pinned: pinnedMemories.Count + pinnedKnowledge.Count,
            Kb: _store.Count(Tier.Hot, ContentType.Knowledge)
                + _store.Count(Tier.Warm, ContentType.Knowledge)
                + _store.Count(Tier.Cold, ContentType.Knowledge),
            Collections: _store.CountKnowledgeCollections());

        // 5. Context string assembly — dynamic detail levels, extractive truncation.
        // Hot tier gets whatever budget the pinned block hasn't consumed.
        var ctxResult = BuildContext(hotEntries, new BuildContextOptions
        {
            TokenBudget = Math.Max(0, _tokenBudget - pinnedTokens),
        });
        var baseContext = ctxResult.Context;
        var hotContextTruncated = ctxResult.Truncated;

        // Phase 2 idea 1c — increment times_injected for every entry that
        // was injected into the host LLM context. Batch update per-tier/type.
        // Include pinned ids so their times_injected increments like hot.
        var injectionTuples = ctxResult.InjectedIds
            .Select(id => (Tier.Hot, ContentType.Memory, id))
            .Concat(pinnedIds)
            .ToList();
        if (injectionTuples.Count > 0)
        {
            try
            {
                _store.UpdateInjectionCounts(injectionTuples);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: injection tracking failed: {ex.Message}");
            }
        }

        // Assemble context: pinned block FIRST, then base/hot context, then skills.
        // Join non-empty parts with "\n\n".
        var parts = new[] { pinnedBlock, baseContext, skillsBlock }
            .Where(p => p.Length > 0);
        var context = string.Join("\n\n", parts);

        // 6. Hints.
        var hints = GenerateHints(_store, warmPromotedIds);
        // Soft-cap warning: when pinned entries consume strictly more than half the
        // budget, advise review. Multiplication avoids integer-division skew on odd
        // budgets. Pins are still injected even if they exceed the entire budget.
        if (pinnedTokens * 2 > _tokenBudget)
        {
            hints = new[]
            {
                new Hint
                {
                    Priority = 1,
                    Type = "pinned_budget_pressure",
                    Summary = $"Pinned entries consume ~{pinnedTokens} of the {_tokenBudget}-token context budget; hot-tier context is being squeezed. Review pins.",
                    SuggestedAction = "memory_unpin",
                },
            }.Concat(hints).ToList();
        }

        // 7. Last session age (humanized). Prefer usage_events MAX(ts) — that
        //    actually tracks session activity per host. Fall back to the
        //    compaction log (last tier movement excl. warm sweep) when no
        //    usage reader is wired (e.g. pure-postgres composition).
        var lastAgeMs = _usageQuery?.GetLastEventTimestampMs()
                        ?? _compactionLog.GetLastTimestampExcludingReason("warm_sweep_decay");
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
            HotContextTruncated: hotContextTruncated,
            SessionAgeHint: null); // fresh session, no hint needed
    }

    private void RunWarmSweep()
    {
        try
        {
            // Phase 2 idea 1c — auto-demote dead-weight hot entries:
            // entries that have been injected many times but never accessed.
            var allHot = _store.List(Tier.Hot, ContentType.Memory);
            var deadWeights = allHot
                .Where(e => e.TimesInjected >= _autoDemoteMinInjections && e.AccessCount == 0)
                .ToList();

            foreach (var entry in deadWeights)
            {
                try
                {
                    _store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, entry.Id);
                }
                catch (InvalidOperationException) { }
            }

            // Existing max-entry enforcement
            var count = _store.Count(Tier.Hot, ContentType.Memory);
            if (count <= _maxEntries) return;

            var excess = count - _maxEntries;
            var toEvict = _store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { OrderBy = "decay_score ASC", Limit = excess });

            // Filter out entries already auto-demoted above
            var deadWeightIds = new HashSet<string>(deadWeights.Select(e => e.Id));
            foreach (var entry in toEvict)
            {
                if (deadWeightIds.Contains(entry.Id)) continue;
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
    /// sorted by <see cref="Entry.DecayScore"/> descending. Uses heuristic token
    /// estimation for budget ceiling checks and optional BERT counting for
    /// precision reporting. Supports dynamic detail levels (Full → Summary →
    /// Compact), extractive truncation when an entry doesn't fit, extractive
    /// summary fallback, compact footer, and injected-ID tracking.
    /// </summary>
    public static BuildContextResult BuildContext(
        IReadOnlyList<Entry> hotEntries,
        BuildContextOptions options)
    {
        if (hotEntries.Count == 0)
            return new BuildContextResult
            {
                Context = string.Empty,
                Truncated = false,
                TokenCount = 0,
                EntriesFull = 0,
                EntriesSummary = 0,
                EntriesCompact = 0,
                InjectedIds = Array.Empty<string>(),
                CompactIds = Array.Empty<string>(),
            };

        var maxTokens = options.TokenBudget;
        var estimateTokens = options.EstimateTokens
            ?? HeuristicEstimateTokens;
        var exactCountTokens = options.CountTokens;
        var autoTag = options.AutoTag;

        var sorted = hotEntries.OrderByDescending(e => e.DecayScore).ToList();
        var result = new StringBuilder();
        var estimatedTokens = 0;
        var first = true;
        var truncated = false;
        var entriesFull = 0;
        var entriesSummary = 0;
        var entriesCompact = 0;
        var injectedIds = new List<string>();
        var compactIds = new List<string>();

        foreach (var e in sorted)
        {
            var remaining = maxTokens - estimatedTokens;
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var detail = GetDetailLevel(e, remaining, estimateTokens, autoTag);
            if (detail is null)
            {
                // Even compact view doesn't fit — try extractive truncation
                var truncatedLine = TruncateEntry(e, remaining, estimateTokens, exactCountTokens, autoTag);
                if (truncatedLine is not null)
                {
                    var lineEst = estimateTokens(truncatedLine);
                    if (estimatedTokens + lineEst > maxTokens)
                    {
                        truncated = true;
                        break;
                    }
                    if (!first) result.Append('\n');
                    result.Append(truncatedLine);
                    estimatedTokens += (first ? 0 : 1) + lineEst;
                    injectedIds.Add(e.Id);
                    entriesSummary++;
                    first = false;
                }
                truncated = true;
                break;
            }

            var entryText = RenderEntry(e, detail.Value, autoTag);
            var entryEst = estimateTokens(entryText);
            var needed = (first ? 0 : 1) + entryEst;

            if (estimatedTokens + needed > maxTokens)
            {
                // Try downgrading to next detail level
                if (detail.Value == DetailLevel.Full)
                {
                    var summaryText = RenderEntry(e, DetailLevel.Summary, autoTag);
                    var summaryEst = estimateTokens(summaryText);
                    var summaryNeeded = (first ? 0 : 1) + summaryEst;
                    if (estimatedTokens + summaryNeeded <= maxTokens)
                    {
                        entryText = summaryText;
                        entryEst = summaryEst;
                        needed = summaryNeeded;
                        detail = DetailLevel.Summary;
                        entriesSummary++;
                    }
                    else
                    {
                        var compactText = RenderEntry(e, DetailLevel.Compact, autoTag);
                        var compactEst = estimateTokens(compactText);
                        var compactNeeded = (first ? 0 : 1) + compactEst;
                        if (estimatedTokens + compactNeeded <= maxTokens)
                        {
                            entryText = compactText;
                            entryEst = compactEst;
                            needed = compactNeeded;
                            detail = DetailLevel.Compact;
                            entriesCompact++;
                            compactIds.Add(e.Id);
                        }
                        else
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
                else if (detail.Value == DetailLevel.Summary)
                {
                    var compactText = RenderEntry(e, DetailLevel.Compact, autoTag);
                    var compactEst = estimateTokens(compactText);
                    var compactNeeded = (first ? 0 : 1) + compactEst;
                    if (estimatedTokens + compactNeeded <= maxTokens)
                    {
                        entryText = compactText;
                        entryEst = compactEst;
                        needed = compactNeeded;
                        detail = DetailLevel.Compact;
                        entriesCompact++;
                        compactIds.Add(e.Id);
                    }
                    else
                    {
                        truncated = true;
                        break;
                    }
                }
                else
                {
                    truncated = true;
                    break;
                }
            }

            if (!first) result.Append('\n');
            result.Append(entryText);
            estimatedTokens += needed;
            injectedIds.Add(e.Id);
            if (detail.Value == DetailLevel.Full) entriesFull++;
            first = false;
        }

        // Compact footer (only if it fits in remaining budget)
        if (compactIds.Count > 0)
        {
            var footer = BuildCompactFooter(compactIds);
            var footerEst = estimateTokens(footer);
            if (estimatedTokens + footerEst <= maxTokens)
            {
                result.Append(footer);
                estimatedTokens += footerEst;
            }
        }

        var context = result.ToString();
        var exactCount = exactCountTokens?.Invoke(context) ?? estimatedTokens;

        return new BuildContextResult
        {
            Context = context,
            Truncated = truncated,
            TokenCount = exactCount,
            EntriesFull = entriesFull,
            EntriesSummary = entriesSummary,
            EntriesCompact = entriesCompact,
            InjectedIds = injectedIds,
            CompactIds = compactIds,
        };
    }

    // ---------- BuildContext helpers ----------

    internal enum DetailLevel { Full, Summary, Compact }

    /// <summary>
    /// Default heuristic: word count * 0.75. Used when no estimate delegate is
    /// provided. ~20% error is acceptable for budget ceiling checks.
    /// </summary>
    internal static int HeuristicEstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 0.75);
    }

    internal static DetailLevel? GetDetailLevel(
        Entry e,
        int remainingBudget,
        Func<string, int> estimateTokens,
        Func<EntryType, string[]?, string?, string>? autoTag)
    {
        var fullText = BuildContextLine(e, autoTag);
        if (estimateTokens(fullText) <= remainingBudget)
            return DetailLevel.Full;

        var summary = e.Summary is not null && FSharpOption<string>.get_IsSome(e.Summary)
            ? e.Summary.Value
            : ExtractFirstSentences(e.Content, 50);
        var summaryText = RenderEntryDetail(e, DetailLevel.Summary, autoTag, summary);
        if (estimateTokens(summaryText) <= remainingBudget)
            return DetailLevel.Summary;

        var compactText = RenderEntryDetail(e, DetailLevel.Compact, autoTag, null);
        if (estimateTokens(compactText) <= remainingBudget)
            return DetailLevel.Compact;

        return null;
    }

    internal static string RenderEntry(Entry e, DetailLevel detail,
        Func<EntryType, string[]?, string?, string>? autoTag)
    {
        var summary = (detail == DetailLevel.Summary && e.Summary is not null
            && FSharpOption<string>.get_IsSome(e.Summary))
            ? e.Summary.Value
            : detail == DetailLevel.Summary
                ? ExtractFirstSentences(e.Content, 50)
                : null;

        return RenderEntryDetail(e, detail, autoTag, summary);
    }

    internal static string RenderEntryDetail(Entry e, DetailLevel detail,
        Func<EntryType, string[]?, string?, string>? autoTag,
        string? summaryFallback)
    {
        if (detail == DetailLevel.Full)
            return BuildContextLine(e, autoTag);
        if (detail == DetailLevel.Compact)
            return FormatCompactEntry(e, autoTag);
        // Summary
        var content = summaryFallback
            ?? (e.Summary is not null && FSharpOption<string>.get_IsSome(e.Summary)
                ? e.Summary.Value
                : ExtractFirstSentences(e.Content, 50));
        return BuildContextLineForContent(e, content, autoTag);
    }

    internal static string BuildContextLine(Entry e,
        Func<EntryType, string[]?, string?, string>? autoTag = null)
    {
        if (autoTag is not null)
        {
            var tags = (IEnumerable<string>)e.Tags;
            var tagList = tags as string[] ?? tags.ToArray();
            return BuildContextLineCommon(e.Id, e.Content, tagList, autoTag(e.EntryType, tagList.Length > 0 ? tagList : null, e.Content));
        }
        return BuildContextLineForContent(e, e.Content, null);
    }

    internal static string BuildContextLineForContent(Entry e, string content,
        Func<EntryType, string[]?, string?, string>? autoTag)
    {
        var tags = (IEnumerable<string>)e.Tags;
        var tagList = tags as string[] ?? tags.ToArray();
        if (autoTag is not null)
        {
            return BuildContextLineCommon(e.Id, content, tagList,
                autoTag(e.EntryType, tagList.Length > 0 ? tagList : null, content));
        }
        return BuildContextLineCommon(e.Id, content, tagList, null);
    }

    private static string BuildContextLineCommon(string id, string content,
        string[] tags, string? tagString)
    {
        var sb = new StringBuilder("- ");
        sb.Append(content);
        if (tagString is not null && tagString.Length > 0)
        {
            sb.Append(' ').Append(tagString);
        }
        else if (tags.Length > 0)
        {
            sb.Append(" [");
            sb.Append(string.Join(", ", tags));
            sb.Append(']');
        }
        return sb.ToString();
    }

    internal static string FormatCompactEntry(Entry e,
        Func<EntryType, string[]?, string?, string>? autoTag)
    {
        var tags = (IEnumerable<string>)e.Tags;
        var tagList = tags as string[] ?? tags.ToArray();
        var tagStr = autoTag is not null
            ? autoTag(e.EntryType, tagList.Length > 0 ? tagList : null, e.Content)
            : FormatCompactTagsBasic(e.EntryType, tagList);
        var project = e.Project is not null && FSharpOption<string>.get_IsSome(e.Project)
            ? $" (project: {e.Project.Value})"
            : "";
        return $"- {tagStr}{project}";
    }

    /// <summary>
    /// Fallback compact tag formatting when AutoTagger is not available.
    /// Uses entry_type + user tags only (no keyword extraction).
    /// </summary>
    internal static string FormatCompactTagsBasic(EntryType entryType, string[] userTags)
    {
        var entryTypeStr = EntryTypeToString(entryType);
        var allTags = new List<string> { entryTypeStr };
        if (userTags.Length > 0)
            allTags.AddRange(userTags.Take(3));
        var tagStr = string.Join(", ", allTags.Distinct());
        if (tagStr.Length <= 60) return $"[{tagStr}]";
        // Truncate
        var result = $"[{entryTypeStr}";
        foreach (var t in allTags.Skip(1))
        {
            var candidate = $"{result}, {t}";
            if (candidate.Length + 1 > 60) break;
            result = candidate;
        }
        return $"{result}]";
    }

    /// <summary>
    /// AOT-safe EntryType to lowercase string. Does NOT use .ToString()
    /// which can fail under NativeAOT trimming of StructuredPrintfImpl.
    /// </summary>
    internal static string EntryTypeToString(EntryType entryType)
    {
        if (entryType.IsCorrection) return "correction";
        if (entryType.IsPreference) return "preference";
        if (entryType.IsDecision) return "decision";
        if (entryType.IsSurfaced) return "surfaced";
        if (entryType.IsImported) return "imported";
        if (entryType.IsCompacted) return "compacted";
        if (entryType.IsIngested) return "ingested";
        return "unknown";
    }

    /// <summary>
    /// Extractive truncation: when an entry doesn't fit in remaining budget,
    /// try to include a sentence-level prefix. Prefer Summary if available.
    /// Returns null if even a minimal fragment doesn't fit.
    /// </summary>
    internal static string? TruncateEntry(
        Entry e,
        int remaining,
        Func<string, int> estimateTokens,
        Func<string, int>? exactCountTokens,
        Func<EntryType, string[]?, string?, string>? autoTag)
    {
        if (remaining <= 20) return null; // minimum fragment threshold

        // Prefer summary if available
        if (e.Summary is not null && FSharpOption<string>.get_IsSome(e.Summary))
        {
            var summary = e.Summary.Value;
            if (estimateTokens(summary) <= remaining)
                return BuildContextLineForContent(e, summary, autoTag);
        }

        // Sentence-level truncation
        var sentences = SplitSentences(e.Content);
        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            var candidate = sb.Length == 0 ? s : sb.ToString() + " " + s;
            var candidateText = BuildContextLineForContent(e, candidate, autoTag);
            if (estimateTokens(candidateText) > remaining)
            {
                if (sb.Length == 0) return null; // first sentence doesn't fit
                break;
            }
            sb.Append(sb.Length == 0 ? s : " " + s);
        }

        if (sb.Length == 0) return null;

        // Verify with exact count if available; bisect if still over
        var text = BuildContextLineForContent(e, sb.ToString() + " [...]", autoTag);
        if (exactCountTokens is not null)
        {
            if (exactCountTokens(text) > remaining)
            {
                // Bisect to make space
                while (sb.Length > 20)
                {
                    sb.Length = sb.Length / 2;
                    text = BuildContextLineForContent(e, sb.ToString() + " [...]", autoTag);
                    if (exactCountTokens(text) <= remaining) break;
                }
            }
        }
        return text;
    }

    /// <summary>
    /// Extracts the first sentences of content up to roughly maxTokens
    /// (estimated via word-count heuristic). Used as summary fallback when
    /// entry.Summary is null.
    /// </summary>
    internal static string ExtractFirstSentences(string content, int maxTokens)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        var sentences = SplitSentences(content);
        var sb = new StringBuilder();
        var charBudget = maxTokens * 4;
        foreach (var s in sentences)
        {
            var candidate = sb.Length == 0 ? s : sb.ToString() + " " + s;
            if (candidate.Length > charBudget) break;
            sb.Append(sb.Length == 0 ? s : " " + s);
        }
        return sb.Length > 0 ? sb.ToString() : content[..Math.Min(content.Length, charBudget)];
    }

    /// <summary>
    /// Split content on sentence boundaries (. ! ? followed by space, or double newline).
    /// </summary>
    private static string[] SplitSentences(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        // Split on sentence-ending punctuation followed by space, or double newline
        var parts = Regex.Split(text, @"(?<=[.!?])\s+|\n\n");
        return parts.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
    }

    internal static string BuildCompactFooter(IReadOnlyList<string> compactIds)
    {
        if (compactIds.Count == 0) return string.Empty;
        var countLabel = compactIds.Count == 1 ? "entry" : "entries";
        return $"\n── {compactIds.Count} {countLabel} in compact view. Use memory_get <id> for details.\n   IDs: {string.Join(", ", compactIds)}";
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
    /// Generates structured actionable hints. Four priorities, capped at 5.
    /// Phase 2 idea 1d: replaces flat truncated strings with Hint DTOs that
    /// carry entry IDs, suggested MCP tool names, and pre-filled arguments.
    /// </summary>
    public static IReadOnlyList<Hint> GenerateHints(
        IStore store,
        IReadOnlyList<string> warmPromotedIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hints = new List<Hint>();

        // Priority 1: corrections + preferences (max 2) — suggest memory_promote.
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
                hints.Add(new Hint
                {
                    Priority = 1,
                    Type = "warm_promotion_candidate",
                    EntryId = entry.Id,
                    Summary = TruncateHint(entry.Content),
                    SuggestedAction = "memory_promote",
                    SuggestedArgs = new Dictionary<string, object> { ["id"] = entry.Id },
                });
        }

        // Priority 2: frequently accessed warm entries (max 2) — suggest memory_search.
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
            hints.Add(new Hint
            {
                Priority = 2,
                Type = "task_relevant_warm",
                EntryId = entry.Id,
                Summary = TruncateHint(entry.Content),
                SuggestedAction = null,
            });
            taken++;
        }

        // Priority 3: recently warm-promoted (max 1).
        foreach (var id in warmPromotedIds.Take(1))
        {
            if (seen.Contains(id)) continue;
            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            if (entry is null) continue;
            seen.Add(entry.Id);
            hints.Add(new Hint
            {
                Priority = 3,
                Type = "session_age",
                EntryId = entry.Id,
                Summary = TruncateHint(entry.Content),
                SuggestedAction = null,
            });
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

    // ---------- RefreshAsync (Phase 1 Step 4) ----------

    /// <summary>
    /// Refreshes hot-tier context mid-session. Recalculates decay scores,
    /// runs warm sweep, re-assembles context, and returns change summary
    /// with efficiency stats. Returns session age hint when session > 30 min.
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(
        string? task = null,
        CancellationToken ct = default)
    {
        if (!IsInitialized)
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
        }

        // Phase 2 idea 2b — embed task for task-aware scoring.
        float[]? taskEmbedding = null;
        if (task is not null && _embedder is not null && _taskWeight > 0)
        {
            try
            {
                taskEmbedding = _embedder.EmbedQuery(task);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: task embedding failed: {ex.Message}");
            }
        }

        // 1. Warm sweep
        var beforeHotCount = _store.Count(Tier.Hot, ContentType.Memory);
        try { RunWarmSweep(); } catch { /* best-effort */ }
        var afterHotCount = _store.Count(Tier.Hot, ContentType.Memory);

        // 2. Re-assemble context
        var hotEntries = _store.List(Tier.Hot, ContentType.Memory);

        // Phase 2 idea 2b — task-aware sort before BuildContext.
        if (taskEmbedding is not null && _taskWeight > 0 && hotEntries.Count > 0)
        {
            var maxDecay = hotEntries.Max(e => e.DecayScore);
            var taskLower = task?.ToLowerInvariant() ?? "";
            var taskWords = new HashSet<string>(
                taskLower.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

            var sorted = hotEntries
                .Select(e =>
                {
                    // Simple text-overlap heuristic for task relevance:
                    // fraction of task words found in entry content or tags.
                    var contentLower = e.Content.ToLowerInvariant();
                    double overlap = 0;
                    if (taskWords.Count > 0)
                    {
                        var matched = taskWords.Count(w => contentLower.Contains(w));
                        overlap = (double)matched / taskWords.Count;
                    }
                    var blend = (1.0 - _taskWeight) * (e.DecayScore / maxDecay) + _taskWeight * overlap;
                    return new { Entry = e, Blend = blend };
                })
                .OrderByDescending(x => x.Blend)
                .Select(x => x.Entry)
                .ToList();
            hotEntries = sorted;
        }

        // 2a. Pinned entries — re-injected on every refresh so that host re-injection
        // after compaction never silently drops pins (spec 2026-06-09).
        var pinnedMemories = _store.List(Tier.Pinned, ContentType.Memory);
        var pinnedKnowledge = _store.List(Tier.Pinned, ContentType.Knowledge);
        var (pinnedBlock, pinnedIds) = PinnedBlockRenderer.Render(pinnedMemories, pinnedKnowledge);
        var pinnedTokens = pinnedBlock.Length > 0 ? HeuristicEstimateTokens(pinnedBlock) : 0;

        var ctxResult = BuildContext(hotEntries, new BuildContextOptions
        {
            // Hot tier gets whatever budget the pinned block hasn't consumed,
            // matching the accounting used in EnsureInitializedAsync.
            TokenBudget = Math.Max(0, _tokenBudget - pinnedTokens),
        });

        // 3. Phase 2 idea 1c — injection tracking on refresh.
        // Include pinned ids so their times_injected increments like hot.
        var refreshInjectionTuples = ctxResult.InjectedIds
            .Select(id => (Tier.Hot, ContentType.Memory, id))
            .Concat(pinnedIds)
            .ToList();
        if (refreshInjectionTuples.Count > 0)
        {
            try
            {
                _store.UpdateInjectionCounts(refreshInjectionTuples);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: refresh injection tracking failed: {ex.Message}");
            }
        }

        // 4. Change summary
        var changes = new ChangeSummary
        {
            Promoted = Array.Empty<ChangeEntry>(),
            Demoted = Array.Empty<ChangeEntry>(),
            Retained = afterHotCount,
            TotalHotEntries = afterHotCount,
        };

        // Hoist sessionAgeMinutes so it is available to both the efficiency
        // block and the sessionAgeHint computation below.
        var sessionAgeMinutes = _sessionStartedAt == DateTimeOffset.MinValue
            ? 0
            : (DateTimeOffset.UtcNow - _sessionStartedAt).TotalMinutes;

        // Phase 3 idea 2a — real session stats (best-effort: failures degrade
        // to zeros/null, never propagate).
        var retrievalCount = 0;
        var avgRetrievalLatencyMs = 0.0;
        if (_retrievalStatsSince is not null && _sessionStartedAt != DateTimeOffset.MinValue)
        {
            try
            {
                (retrievalCount, avgRetrievalLatencyMs) =
                    _retrievalStatsSince(_sessionStartedAt.ToUnixTimeMilliseconds());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: retrieval stats failed: {ex.Message}");
            }
        }

        // No _sessionStartedAt guard here (unlike retrieval stats): RefreshAsync
        // always runs EnsureInitializedAsync first, and ToolCacheStore counters
        // are per-process == per-session for a stdio MCP server.
        CacheStats? cacheBlock = null;
        if (_cacheStats is not null)
        {
            try
            {
                var (hits, misses, saved) = _cacheStats();
                var total = hits + misses;
                cacheBlock = new CacheStats
                {
                    Hits = hits,
                    Misses = misses,
                    TokensSaved = saved,
                    HitRate = total > 0 ? (double)hits / total : 0.0,
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"total-recall: cache stats failed: {ex.Message}");
            }
        }

        var efficiency = new EfficiencyStats
        {
            HotTierUtilization = new HotTierUtilization
            {
                EntriesUsed = hotEntries.Count,
                MaxEntries = _maxEntries,
                TokensUsed = ctxResult.TokenCount,
                TokenBudget = _tokenBudget,
            },
            // All InjectionImpact fields are TOKEN counts, not entry counts —
            // spec §7.3.1: memoriesInjected + skillsInjected + hintsInjected
            // sum to totalInjected. (The name reads like an entry count; it
            // is not. Entry counts live in HotTierUtilization.EntriesUsed.)
            // Pinned tokens are included in MemoriesInjected + TotalInjected
            // so the invariant memories+skills+hints == total is preserved.
            InjectionImpact = new InjectionImpact
            {
                MemoriesInjected = ctxResult.TokenCount + pinnedTokens,
                SkillsInjected = 0,
                HintsInjected = 0,
                TotalInjected = ctxResult.TokenCount + pinnedTokens,
            },
            Cache = cacheBlock,
            Session = new SessionInfo
            {
                DurationMinutes = sessionAgeMinutes,
                RetrievalsPerformed = retrievalCount,
                AvgRetrievalLatencyMs = avgRetrievalLatencyMs,
            },
        };

        // NOTE: the session_refresh advisory intentionally appears in BOTH
        // Recommendations and the legacy SessionAgeHint field — both are
        // spec-defined wire fields (§7.3.1 / §5.3.3); hint is the pre-Phase-3
        // channel kept for consumers that don't read recommendations yet.
        // Phase 3 idea 2a — advisory recommendations (spec rules out
        // auto-adaptation; the agent decides).
        var recommendations = new List<Recommendation>();
        if (sessionAgeMinutes > SessionRefreshThresholdMinutes)
            recommendations.Add(new Recommendation
            {
                Action = "session_refresh",
                Reason = $"Session is {sessionAgeMinutes:F0} minutes old — decay scores have shifted",
            });
        if (retrievalCount >= RetrievalExtractThreshold)
            recommendations.Add(new Recommendation
            {
                Action = "memory_extract",
                Reason = $"Session has {retrievalCount} retrievals — consider extracting key findings",
            });

        string? sessionAgeHint = null;
        if (sessionAgeMinutes > 30)
        {
            sessionAgeHint = $"Session is {sessionAgeMinutes:F0} minutes old. Consider calling session_refresh for updated decay scores.";
        }

        // Assemble refresh context: pinned block FIRST (matching init join style),
        // then hot context. Non-empty parts joined with "\n\n".
        var refreshParts = new[] { pinnedBlock, ctxResult.Context }.Where(p => p.Length > 0);
        var refreshContext = string.Join("\n\n", refreshParts);

        return new RefreshResult
        {
            Context = refreshContext,
            ContextTruncated = ctxResult.Truncated,
            TokenCount = ctxResult.TokenCount + pinnedTokens,
            TokenBudget = _tokenBudget,
            Changes = changes,
            Efficiency = efficiency,
            SessionAgeHint = sessionAgeHint,
            Recommendations = recommendations,
        };
    }
}

// ---------- BuildContext config types ----------

/// <summary>
/// Options controlling <see cref="SessionLifecycle.BuildContext"/> behavior.
/// </summary>
public sealed record BuildContextOptions
{
    public int TokenBudget { get; init; } = 4000;
    public Func<string, int>? CountTokens { get; init; }
    public Func<string, int> EstimateTokens { get; init; } =
        SessionLifecycle.HeuristicEstimateTokens;
    public Func<EntryType, string[]?, string?, string>? AutoTag { get; init; }
}

/// <summary>
/// Result of <see cref="SessionLifecycle.BuildContext"/>. Includes token
/// counts, detail-level breakdowns, injected IDs, and compact IDs.
/// </summary>
public sealed record BuildContextResult
{
    public string Context { get; init; } = "";
    public bool Truncated { get; init; }
    public int TokenCount { get; init; }
    public int EntriesFull { get; init; }
    public int EntriesSummary { get; init; }
    public int EntriesCompact { get; init; }
    public IReadOnlyList<string> InjectedIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompactIds { get; init; } = Array.Empty<string>();
}

// ---------- Phase 2 idea 1d: Structured Hint DTO ----------

/// <summary>
/// Structured actionable hint for the host LLM. Replaces the flat
/// truncated-content strings used before Phase 2. Each hint carries
/// a priority, type, entry reference, summary, and optional suggested
/// MCP tool invocation (name + pre-filled arguments).
/// </summary>
public sealed record Hint
{
    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("entryId")]
    public string? EntryId { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    [JsonPropertyName("suggestedAction")]
    public string? SuggestedAction { get; init; }

    [JsonPropertyName("suggestedArgs")]
    public IReadOnlyDictionary<string, object>? SuggestedArgs { get; init; }
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
    [property: JsonPropertyName("hints")] IReadOnlyList<Hint> Hints,
    [property: JsonPropertyName("lastSessionAge")] string? LastSessionAge,
    [property: JsonPropertyName("smokeTest")] SmokeTestResult? SmokeTest,
    [property: JsonPropertyName("regressionAlerts")] IReadOnlyList<RegressionAlert>? RegressionAlerts,
    [property: JsonPropertyName("storage")] string Storage,
    [property: JsonPropertyName("hotContextTruncated")] bool HotContextTruncated,
    [property: JsonPropertyName("sessionAgeHint")] string? SessionAgeHint);

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
    [property: JsonPropertyName("pinned")] int Pinned,
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

// ---------- Phase 1 Step 4: Refresh DTOs ----------

/// <summary>
/// Result of <see cref="SessionLifecycle.RefreshAsync"/>. Carries updated
/// context, a compact change summary, and efficiency stats.
/// </summary>
public sealed record RefreshResult
{
    [JsonPropertyName("context")]
    public string Context { get; init; } = "";

    [JsonPropertyName("contextTruncated")]
    public bool ContextTruncated { get; init; }

    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; init; }

    [JsonPropertyName("tokenBudget")]
    public int TokenBudget { get; init; }

    [JsonPropertyName("changes")]
    public ChangeSummary Changes { get; init; } = new();

    [JsonPropertyName("efficiency")]
    public EfficiencyStats Efficiency { get; init; } = new();

    [JsonPropertyName("sessionAgeHint")]
    public string? SessionAgeHint { get; init; }

    [JsonPropertyName("recommendations")]
    public IReadOnlyList<Recommendation> Recommendations { get; init; } = Array.Empty<Recommendation>();
}

public sealed record ChangeSummary
{
    [JsonPropertyName("promoted")]
    public IReadOnlyList<ChangeEntry> Promoted { get; init; } = Array.Empty<ChangeEntry>();

    [JsonPropertyName("demoted")]
    public IReadOnlyList<ChangeEntry> Demoted { get; init; } = Array.Empty<ChangeEntry>();

    [JsonPropertyName("retained")]
    public int Retained { get; init; }

    [JsonPropertyName("totalHotEntries")]
    public int TotalHotEntries { get; init; }
}

public sealed record ChangeEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}

public sealed record EfficiencyStats
{
    [JsonPropertyName("hotTierUtilization")]
    public HotTierUtilization HotTierUtilization { get; init; } = new();

    [JsonPropertyName("injectionImpact")]
    public InjectionImpact InjectionImpact { get; init; } = new();

    [JsonPropertyName("cache"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheStats? Cache { get; init; }

    [JsonPropertyName("session")]
    public SessionInfo Session { get; init; } = new();
}

public sealed record HotTierUtilization
{
    [JsonPropertyName("entriesUsed")]
    public int EntriesUsed { get; init; }

    [JsonPropertyName("maxEntries")]
    public int MaxEntries { get; init; }

    [JsonPropertyName("tokensUsed")]
    public int TokensUsed { get; init; }

    [JsonPropertyName("tokenBudget")]
    public int TokenBudget { get; init; }
}

/// <summary>Token-denominated injection breakdown (spec §7.3.1): each field
/// counts TOKENS contributed by that source; they sum to TotalInjected.</summary>
public sealed record InjectionImpact
{
    [JsonPropertyName("memoriesInjected")]
    public int MemoriesInjected { get; init; }

    [JsonPropertyName("skillsInjected")]
    public int SkillsInjected { get; init; }

    [JsonPropertyName("hintsInjected")]
    public int HintsInjected { get; init; }

    [JsonPropertyName("totalInjected")]
    public int TotalInjected { get; init; }
}

public sealed record SessionInfo
{
    [JsonPropertyName("durationMinutes")]
    public double DurationMinutes { get; init; }

    [JsonPropertyName("retrievalsPerformed")]
    public int RetrievalsPerformed { get; init; }

    [JsonPropertyName("avgRetrievalLatencyMs")]
    public double AvgRetrievalLatencyMs { get; init; }
}

/// <summary>Phase 3 idea 2a — per-session tool-cache economics.</summary>
public sealed record CacheStats
{
    [JsonPropertyName("hits")]
    public long Hits { get; init; }

    [JsonPropertyName("misses")]
    public long Misses { get; init; }

    [JsonPropertyName("tokensSaved")]
    public long TokensSaved { get; init; }

    [JsonPropertyName("hitRate")]
    public double HitRate { get; init; }
}

/// <summary>Phase 3 idea 2a — advisory next-action suggestion.</summary>
public sealed record Recommendation
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}
