using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.FSharp.Core;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Config;

/// <summary>
/// Raised when <c>TOTAL_RECALL_DB_PATH</c> fails validation. Mirrors the TS
/// <c>SqliteDbPathError</c> — callers can distinguish config-time failures
/// from runtime SQLite errors via type.
/// </summary>
public sealed class SqliteDbPathException : Exception
{
    public SqliteDbPathException(string message) : base(message) { }
}

/// <summary>
/// Loads <see cref="Core.Config.TotalRecallConfig"/> from TOML sources.
///
/// Loads an embedded <c>defaults.toml</c> resource and optionally deep-merges a
/// user-supplied <c>config.toml</c> over it. Parsing uses Tomlyn's syntax-tree
/// path (<see cref="Toml.Parse(string, string, TomlParserOptions)"/> followed by
/// <see cref="TomlSyntaxExtensions.ToModel"/>) which is AOT-clean — the
/// reflection-based <c>Toml.ToModel&lt;T&gt;</c> overload is deliberately NOT
/// used because it is not trim-safe.
///
/// This class does NOT own snapshotting (<c>createConfigSnapshot</c>) — see
/// <see cref="TotalRecall.Infrastructure.Eval.ConfigSnapshotStore"/> (Task 5.3b).
/// <see cref="ConfigWriter"/> (Task 5.8) owns the set path
/// (<c>setNestedKey</c> / <c>saveUserConfig</c>).
/// </summary>
public interface IConfigLoader
{
    Core.Config.TotalRecallConfig LoadDefaults();
    Core.Config.TotalRecallConfig LoadEffectiveConfig(string? userConfigPath = null);

    /// <summary>
    /// Like <see cref="LoadEffectiveConfig"/>, but returns the raw merged
    /// <see cref="TomlTable"/> instead of projecting through
    /// <see cref="Core.Config.TotalRecallConfig"/>. Used by <c>config get</c>
    /// to walk arbitrary dotted paths — the projected record can only
    /// represent the statically known schema.
    /// </summary>
    TomlTable LoadEffectiveTable(string? userConfigPath = null);
}

/// <inheritdoc cref="IConfigLoader"/>
public sealed class ConfigLoader : IConfigLoader
{
    /// <summary>
    /// Returns the data directory for total-recall. Honors
    /// <c>TOTAL_RECALL_HOME</c> if set, otherwise <c>$HOME/.total-recall</c>.
    /// </summary>
    public static string GetDataDir()
    {
        var explicitHome = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        if (!string.IsNullOrEmpty(explicitHome)) return explicitHome;
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? "~";
        return Path.Combine(home, ".total-recall");
    }

    /// <summary>
    /// Resolve the SQLite database file path.
    ///
    /// Precedence:
    /// <list type="number">
    ///   <item><c>TOTAL_RECALL_DB_PATH</c> (validated, leading <c>~/</c> expanded)</item>
    ///   <item><c>&lt;GetDataDir&gt;/total-recall.db</c></item>
    /// </list>
    ///
    /// Rules:
    /// <list type="bullet">
    ///   <item>Must be an absolute file path, or start with <c>~/</c>.</item>
    ///   <item>Trailing <c>/</c> or <c>\</c> is rejected (file path required).</item>
    ///   <item>Bare <c>~</c> is rejected (no filename).</item>
    ///   <item>Empty / whitespace values are treated as unset.</item>
    /// </list>
    ///
    /// Throws <see cref="SqliteDbPathException"/> on any validation failure.
    /// Pure function: no filesystem probes; parent-dir creation happens at
    /// the call site.
    /// </summary>
    public static string GetDbPath()
    {
        var raw = Environment.GetEnvironmentVariable("TOTAL_RECALL_DB_PATH");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Path.Combine(GetDataDir(), "total-recall.db");
        }

        return ValidateDbPath(raw, "TOTAL_RECALL_DB_PATH");
    }

    /// <summary>
    /// The validation shared by <c>TOTAL_RECALL_DB_PATH</c> and <c>transcripts.db_path</c>:
    /// trim, reject a trailing separator and a bare <c>~</c>, expand a leading
    /// <c>~/</c>, and require an absolute path. Throws <see cref="SqliteDbPathException"/>;
    /// the message names <paramref name="settingName"/>.
    /// </summary>
    internal static string ValidateDbPath(string raw, string settingName)
    {
        var trimmed = raw.Trim();

        if (trimmed.EndsWith('/') || trimmed.EndsWith('\\'))
        {
            throw new SqliteDbPathException(
                $"{settingName} must be a file path, not a directory. Got: \"{trimmed}\"");
        }

        if (trimmed == "~")
        {
            throw new SqliteDbPathException(
                $"{settingName} must be a file path, not a directory. Got: \"~\"");
        }

        var expanded = trimmed;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? string.Empty;
            // Normalize the rest-of-path's separators to the platform's
            // convention before combining — Path.Combine does NOT rewrite
            // internal separators of its arguments, so a "~/tr/memories.db"
            // on Windows would otherwise leave forward slashes mixed in.
            var rest = trimmed.Substring(2).Replace('/', Path.DirectorySeparatorChar);
            expanded = Path.Combine(home, rest);
        }

        if (!Path.IsPathRooted(expanded))
        {
            throw new SqliteDbPathException(
                $"{settingName} must be absolute or start with ~/. Got: \"{trimmed}\"");
        }

        return expanded;
    }

    /// <summary>
    /// Read <c>eval.grow.sensitive_terms</c> from the effective config — the
    /// caller-supplied internal-term denylist for the eval_grow accept guard.
    /// Defaults to an empty list (no internal names ship in <c>defaults.toml</c>).
    /// Blank entries are dropped.
    /// </summary>
    public static IReadOnlyList<string> LoadGrowSensitiveTerms(string? userConfigPath = null)
    {
        var table = new ConfigLoader().LoadEffectiveTable(userConfigPath);
        if (table.TryGetValue("eval", out var evalObj) && evalObj is TomlTable evalTable
            && evalTable.TryGetValue("grow", out var growObj) && growObj is TomlTable growTable
            && growTable.TryGetValue("sensitive_terms", out var termsObj) && termsObj is TomlArray arr)
        {
            var list = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item is string s && !string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list;
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Read <c>updates.notice</c> from the effective config — whether the
    /// "what's new" notice is shown. True unless the value is the boolean
    /// <c>false</c>; a missing table, a value of another type, or an unreadable
    /// or unparsable config file all read as true (the notice stays on).
    /// </summary>
    public static bool LoadUpdatesNoticeEnabled(string? userConfigPath = null)
    {
        try
        {
            var table = new ConfigLoader().LoadEffectiveTable(userConfigPath);
            if (table.TryGetValue("updates", out var updatesObj) && updatesObj is TomlTable updatesTable
                && updatesTable.TryGetValue("notice", out var noticeObj) && noticeObj is bool notice)
            {
                return notice;
            }
        }
        catch (Exception)
        {
            // Any read or parse problem leaves the notice on.
        }
        return true;
    }

    /// <summary>
    /// Read the <c>[transcripts]</c> settings of the opt-in archive from the effective
    /// config. Fail-safe: a missing table, a value of another type, or an unreadable or
    /// unparsable config file reads as the defaults (disabled, main database, no
    /// retention, no remote embedding). <c>enabled</c> and <c>allow_remote_embedding</c>
    /// are on only for the boolean <c>true</c>; <c>retention_days</c> below 1 reads 0.
    /// <c>db_path</c> is validated with the rules of <c>TOTAL_RECALL_DB_PATH</c>: a
    /// non-string or invalid value sets <c>DbPathError</c> and leaves <c>DbPath</c> empty.
    /// </summary>
    public static TranscriptsSettings LoadTranscriptsSettings(string? userConfigPath = null)
    {
        try
        {
            var table = new ConfigLoader().LoadEffectiveTable(userConfigPath);
            if (!table.TryGetValue("transcripts", out var tObj) || tObj is not TomlTable t)
            {
                return new TranscriptsSettings(false, string.Empty, null, 0, false);
            }

            var enabled = t.TryGetValue("enabled", out var e) && e is bool eb && eb;
            var allowRemote = t.TryGetValue("allow_remote_embedding", out var a) && a is bool ab && ab;

            var retention = 0;
            if (t.TryGetValue("retention_days", out var r) && r is long rl && rl > 0)
            {
                retention = rl > int.MaxValue ? int.MaxValue : (int)rl;
            }

            var dbPath = string.Empty;
            string? dbPathError = null;
            if (t.TryGetValue("db_path", out var d))
            {
                if (d is string ds)
                {
                    if (!string.IsNullOrWhiteSpace(ds))
                    {
                        try { dbPath = ValidateDbPath(ds, "transcripts.db_path"); }
                        catch (SqliteDbPathException ex) { dbPathError = ex.Message; }
                    }
                }
                else
                {
                    dbPathError = "transcripts.db_path must be a string.";
                }
            }

            return new TranscriptsSettings(enabled, dbPath, dbPathError, retention, allowRemote);
        }
        catch (Exception)
        {
            // Any read or parse problem leaves the archive off.
            return new TranscriptsSettings(false, string.Empty, null, 0, false);
        }
    }

    /// <summary>
    /// Whether the background update check is on (<c>updates.check</c>). On unless the
    /// effective config holds the boolean false; a missing table, a value of another
    /// type, or an unreadable or unparsable config file all read as true.
    /// </summary>
    public static bool LoadUpdatesCheckEnabled(string? userConfigPath = null)
    {
        try
        {
            var table = new ConfigLoader().LoadEffectiveTable(userConfigPath);
            if (table.TryGetValue("updates", out var updatesObj) && updatesObj is TomlTable updatesTable
                && updatesTable.TryGetValue("check", out var checkObj) && checkObj is bool check)
            {
                return check;
            }
        }
        catch (Exception)
        {
            // Any read or parse problem leaves the check on.
        }
        return true;
    }

    /// <summary>
    /// Minimum hours between update checks (<c>updates.check_interval_hours</c>): a whole
    /// number from 1 to 8760, anything else (or any read problem) is 24.
    /// </summary>
    public static int LoadUpdatesCheckIntervalHours(string? userConfigPath = null)
    {
        try
        {
            var table = new ConfigLoader().LoadEffectiveTable(userConfigPath);
            if (table.TryGetValue("updates", out var updatesObj) && updatesObj is TomlTable updatesTable
                && updatesTable.TryGetValue("check_interval_hours", out var raw))
            {
                return WhatsNew.UpdateSettings.NormalizeIntervalHours(raw);
            }
        }
        catch (Exception)
        {
            // Any read or parse problem means the default interval.
        }
        return WhatsNew.UpdateSettings.DefaultIntervalHours;
    }

    /// <inheritdoc/>
    public Core.Config.TotalRecallConfig LoadDefaults()
    {
        var table = ParseToml(LoadDefaultsToml(), "defaults.toml");
        return Project(table);
    }

    /// <inheritdoc/>
    public Core.Config.TotalRecallConfig LoadEffectiveConfig(string? userConfigPath = null)
    {
        return Project(LoadEffectiveTable(userConfigPath));
    }

    /// <inheritdoc/>
    public TomlTable LoadEffectiveTable(string? userConfigPath = null)
    {
        var defaultsTable = ParseToml(LoadDefaultsToml(), "defaults.toml");

        var resolvedUserPath = userConfigPath ?? Path.Combine(GetDataDir(), "config.toml");
        if (File.Exists(resolvedUserPath))
        {
            var userText = File.ReadAllText(resolvedUserPath);
            var userTable = ParseToml(userText, resolvedUserPath);
            return MergeTables(defaultsTable, userTable);
        }

        return defaultsTable;
    }

    // --- parsing ----------------------------------------------------------

    private static string LoadDefaultsToml()
    {
        var assembly = typeof(ConfigLoader).Assembly;
        var resourceName = $"{assembly.GetName().Name}.Config.defaults.toml";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static TomlTable ParseToml(string text, string sourceName)
    {
        DocumentSyntax doc;
        try
        {
            doc = Toml.Parse(text, sourceName);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"Failed to parse TOML from {sourceName}: {ex.Message}", ex);
        }

        if (doc.HasErrors)
        {
            var errors = string.Join("; ", doc.Diagnostics.Select(d => d.ToString()));
            throw new InvalidDataException(
                $"Invalid TOML in {sourceName}: {errors}");
        }

        return doc.ToModel();
    }

    // --- merge ------------------------------------------------------------

    /// <summary>
    /// Deep-merges <paramref name="source"/> over <paramref name="target"/>.
    /// Nested <see cref="TomlTable"/>s are merged recursively; scalar and array
    /// values from source replace target. Unsafe keys
    /// (<c>__proto__</c>, <c>constructor</c>, <c>prototype</c>) are skipped.
    /// Mirrors <c>Core.Config.deepMerge</c> and the TS <c>deepMerge</c>.
    /// </summary>
    private static TomlTable MergeTables(TomlTable target, TomlTable source)
    {
        var result = new TomlTable();
        foreach (var kv in target)
        {
            result[kv.Key] = kv.Value;
        }
        foreach (var kv in source)
        {
            if (!IsSafeKey(kv.Key)) continue;
            if (result.TryGetValue(kv.Key, out var existing)
                && existing is TomlTable existingTable
                && kv.Value is TomlTable sourceTable)
            {
                result[kv.Key] = MergeTables(existingTable, sourceTable);
            }
            else
            {
                result[kv.Key] = kv.Value;
            }
        }
        return result;
    }

    private static bool IsSafeKey(string key) =>
        key != "__proto__" && key != "constructor" && key != "prototype";

    // --- projection -------------------------------------------------------

    private static Core.Config.TotalRecallConfig Project(TomlTable table)
    {
        var tiers = GetTable(table, "tiers");
        var hot = GetTable(tiers, "hot");
        var warm = GetTable(tiers, "warm");
        var cold = GetTable(tiers, "cold");

        // F# record constructors expose positional args to C#, not named.
        var hotCfg = new Core.Config.HotTierConfig(
            GetInt(hot, "max_entries"),
            GetInt(hot, "token_budget"),
            GetDouble(hot, "carry_forward_threshold"),
            hot.TryGetValue("task_weight", out var tw)
                ? Convert.ToDouble(tw)
                : 0.0,
            hot.TryGetValue("compaction_hint_threshold", out var cht)
                ? Convert.ToInt32(cht)
                : 5,
            hot.TryGetValue("max_content_chars", out var mcc)
                ? Convert.ToInt32(mcc)
                : 1200);

        var warmCfg = new Core.Config.WarmTierConfig(
            GetInt(warm, "max_entries"),
            GetInt(warm, "retrieval_top_k"),
            GetDouble(warm, "similarity_threshold"),
            GetInt(warm, "cold_decay_days"));

        var coldCfg = new Core.Config.ColdTierConfig(
            GetInt(cold, "chunk_max_tokens"),
            GetInt(cold, "chunk_overlap_tokens"),
            GetInt(cold, "lazy_summary_threshold"));

        // Pinned tier config is optional — absent in existing config files.
        var pinnedCfgOpt = Microsoft.FSharp.Core.FSharpOption<Core.Config.PinnedTierConfig>.None;
        if (tiers.TryGetValue("pinned", out var pinnedObj) && pinnedObj is TomlTable pinnedTable)
        {
            int maxContentChars;
            try
            {
                var mccOpt = TryGetInt(pinnedTable, "max_content_chars");
                maxContentChars = Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(mccOpt)
                    ? mccOpt.Value
                    : 500;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException(
                    "tiers.pinned.max_content_chars must be an integer", ex);
            }

            bool floorEnabled;
            int floorEveryN;
            int floorGrowth;
            bool projectScoping;
            try
            {
                var feOpt = TryGetBool(pinnedTable, "floor_enabled");
                floorEnabled = Microsoft.FSharp.Core.FSharpOption<bool>.get_IsSome(feOpt)
                    ? feOpt.Value
                    : true;
                var fenOpt = TryGetInt(pinnedTable, "floor_every_n_turns");
                floorEveryN = Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(fenOpt)
                    ? fenOpt.Value
                    : 6;
                var fgtOpt = TryGetInt(pinnedTable, "floor_growth_tokens");
                floorGrowth = Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(fgtOpt)
                    ? fgtOpt.Value
                    : 6000;
                var psOpt = TryGetBool(pinnedTable, "project_scoping");
                projectScoping = Microsoft.FSharp.Core.FSharpOption<bool>.get_IsSome(psOpt)
                    ? psOpt.Value
                    : true;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException(
                    "tiers.pinned floor_* keys must be the correct type (bool/int)", ex);
            }

            pinnedCfgOpt = Microsoft.FSharp.Core.FSharpOption<Core.Config.PinnedTierConfig>.Some(
                new Core.Config.PinnedTierConfig(maxContentChars, floorEnabled, floorEveryN, floorGrowth, projectScoping));
        }

        var tiersCfg = new Core.Config.TiersConfig(hotCfg, warmCfg, coldCfg, pinnedCfgOpt);

        var compaction = GetTable(table, "compaction");
        var compactionCfg = new Core.Config.CompactionConfig(
            GetDouble(compaction, "decay_half_life_hours"),
            GetDouble(compaction, "warm_threshold"),
            GetDouble(compaction, "promote_threshold"),
            GetInt(compaction, "warm_sweep_interval_days"),
            TryGetDouble(compaction, "decay_half_life_correction"),
            TryGetDouble(compaction, "decay_half_life_preference"),
            TryGetDouble(compaction, "decay_half_life_surfaced"),
            TryGetDouble(compaction, "decay_half_life_decision"),
            compaction.TryGetValue("auto_demote_min_injections", out var admi)
                ? Convert.ToInt32(admi)
                : 10,
            compaction.TryGetValue("promote_min_access", out var pma)
                ? Convert.ToInt32(pma)
                : 5);

        var embedding = GetTable(table, "embedding");
        var embeddingCfg = new Core.Config.EmbeddingConfig(
            GetString(embedding, "model"),
            GetInt(embedding, "dimensions"),
            TryGetString(embedding, "provider"),
            TryGetString(embedding, "endpoint"),
            TryGetString(embedding, "bedrock_region"),
            TryGetString(embedding, "bedrock_model"),
            TryGetString(embedding, "model_name"),
            TryGetString(embedding, "api_key"),
            TryGetString(embedding, "query_prefix"),
            TryGetString(embedding, "on_model_change"));

        FSharpOption<Core.Config.RegressionConfig> regression;
        if (table.TryGetValue("regression", out var regObj) && regObj is TomlTable regTable)
        {
            // Lenient: a value that is not a number (or a min_events that is not a
            // whole number in int range) is ignored so RegressionOptions falls back
            // to the shipped default, instead of failing the whole config load.
            var regCfg = new Core.Config.RegressionConfig(
                TryGetNumberLenient(regTable, "miss_rate_delta"),
                TryGetNumberLenient(regTable, "latency_ratio"),
                TryGetWholeNumberLenient(regTable, "min_events"));
            regression = FSharpOption<Core.Config.RegressionConfig>.Some(regCfg);
        }
        else
        {
            regression = FSharpOption<Core.Config.RegressionConfig>.None;
        }

        FSharpOption<Core.Config.SearchConfig> search;
        if (table.TryGetValue("search", out var searchObj) && searchObj is TomlTable searchTable)
        {
            var searchCfg = new Core.Config.SearchConfig(
                TryGetDouble(searchTable, "fts_weight"));
            search = FSharpOption<Core.Config.SearchConfig>.Some(searchCfg);
        }
        else
        {
            search = FSharpOption<Core.Config.SearchConfig>.None;
        }

        FSharpOption<Core.Config.StorageConfig> storage;
        if (table.TryGetValue("storage", out var storageObj) && storageObj is TomlTable storageTable)
        {
            var storageCfg = new Core.Config.StorageConfig(
                TryGetString(storageTable, "connection_string"),
                TryGetString(storageTable, "mode"));
            storage = FSharpOption<Core.Config.StorageConfig>.Some(storageCfg);
        }
        else
        {
            storage = FSharpOption<Core.Config.StorageConfig>.None;
        }

        FSharpOption<Core.Config.CortexConfig> cortex;
        if (table.TryGetValue("cortex", out var cortexObj) && cortexObj is TomlTable cortexTable)
        {
            var url = TryGetString(cortexTable, "url");
            var pat = TryGetString(cortexTable, "pat");

            // Env var overrides
            var envUrl = Environment.GetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL");
            if (!string.IsNullOrEmpty(envUrl))
                url = FSharpOption<string>.Some(envUrl);

            var envPat = Environment.GetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT");
            if (!string.IsNullOrEmpty(envPat))
                pat = FSharpOption<string>.Some(envPat);

            if (FSharpOption<string>.get_IsSome(url) && FSharpOption<string>.get_IsSome(pat))
            {
                var syncInterval = TryGetInt(cortexTable, "sync_interval_seconds");
                var cortexCfg = new Core.Config.CortexConfig(url.Value, pat.Value, syncInterval);
                cortex = FSharpOption<Core.Config.CortexConfig>.Some(cortexCfg);
            }
            else
            {
                cortex = FSharpOption<Core.Config.CortexConfig>.None;
            }
        }
        else
        {
            // Check env vars even without [cortex] section
            var envUrl = Environment.GetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL");
            var envPat = Environment.GetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT");
            if (!string.IsNullOrEmpty(envUrl) && !string.IsNullOrEmpty(envPat))
            {
                var cortexCfg = new Core.Config.CortexConfig(envUrl, envPat, FSharpOption<int>.None);
                cortex = FSharpOption<Core.Config.CortexConfig>.Some(cortexCfg);
            }
            else
            {
                cortex = FSharpOption<Core.Config.CortexConfig>.None;
            }
        }

        FSharpOption<Core.Config.UserConfig> user;
        if (table.TryGetValue("user", out var userObj) && userObj is TomlTable userTable)
        {
            var userCfg = new Core.Config.UserConfig(
                TryGetString(userTable, "user_id"));
            user = FSharpOption<Core.Config.UserConfig>.Some(userCfg);
        }
        else
        {
            user = FSharpOption<Core.Config.UserConfig>.None;
        }

        FSharpOption<Core.Config.ScopeConfig> scope;
        if (table.TryGetValue("scope", out var scopeObj) && scopeObj is TomlTable scopeTable)
        {
            var scopeCfg = new Core.Config.ScopeConfig(
                TryGetString(scopeTable, "default"));
            scope = FSharpOption<Core.Config.ScopeConfig>.Some(scopeCfg);
        }
        else
        {
            scope = FSharpOption<Core.Config.ScopeConfig>.None;
        }

        FSharpOption<Core.Config.SkillConfig> skill;
        // Accept both [skills] (documented) and [skill] (legacy singular) for robustness.
        if ((table.TryGetValue("skills", out var skillObj) || table.TryGetValue("skill", out skillObj))
            && skillObj is TomlTable skillTable)
        {
            var extraDirs = TryGetStringArray(skillTable, "extra_dirs");
            var autoImport = TryGetBool(skillTable, "auto_import");
            var skillCfg = new Core.Config.SkillConfig(extraDirs, autoImport);
            skill = FSharpOption<Core.Config.SkillConfig>.Some(skillCfg);
        }
        else
        {
            skill = FSharpOption<Core.Config.SkillConfig>.None;
        }

        // [tool_cache] always exists in the merged table because defaults.toml
        // ships it and user configs deep-merge over defaults — so GetInt's
        // throw path and the None branch are only reachable when Project is
        // called on a hand-constructed table (defensive, not a config surface).
        FSharpOption<Core.Config.ToolCacheConfig> toolCache;
        if (table.TryGetValue("tool_cache", out var tcObj) && tcObj is TomlTable tcTable)
        {
            var tcCfg = new Core.Config.ToolCacheConfig(
                GetInt(tcTable, "max_entries"),
                GetInt(tcTable, "default_ttl_seconds"));
            toolCache = FSharpOption<Core.Config.ToolCacheConfig>.Some(tcCfg);
        }
        else
        {
            toolCache = FSharpOption<Core.Config.ToolCacheConfig>.None;
        }

        return new Core.Config.TotalRecallConfig(
            tiersCfg,
            compactionCfg,
            embeddingCfg,
            regression,
            search,
            storage,
            user,
            cortex,
            scope,
            skill,
            toolCache,
            ProjectGitSync(table));
    }

    // --- [git_sync] -------------------------------------------------------

    /// <summary>
    /// Reads the optional <c>[git_sync]</c> table. Absent (or not a table) gives <c>None</c>. Otherwise every key
    /// is read as a raw value and normalised by <see cref="Core.Config.GitSyncSettings.normalise"/>: a wrong type
    /// or an out-of-range value never throws here, it becomes a default plus a warning. A TOML syntax error is
    /// raised earlier, by the parse.
    /// </summary>
    private static FSharpOption<Core.Config.GitSyncConfig> ProjectGitSync(TomlTable table)
    {
        if (!table.TryGetValue("git_sync", out var obj) || obj is not TomlTable gs)
            return FSharpOption<Core.Config.GitSyncConfig>.None;

        var raw = new Core.Config.RawGitSync(
            GitRaw(gs, "remote"),
            GitRaw(gs, "branch"),
            GitRaw(gs, "interval_seconds"),
            GitRaw(gs, "session_start"),
            GitRaw(gs, "session_start_soft_seconds"),
            GitRaw(gs, "session_start_max_seconds"),
            GitRaw(gs, "session_start_stall_seconds"),
            GitRaw(gs, "session_end"),
            GitRaw(gs, "session_end_seconds"),
            GitRaw(gs, "delete_guard_percent"));
        return FSharpOption<Core.Config.GitSyncConfig>.Some(Core.Config.GitSyncSettings.normalise(raw));
    }

    private static Core.Config.GitRawValue GitRaw(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var v))
            return Core.Config.GitRawValue.RawAbsent;
        return v switch
        {
            string s => Core.Config.GitRawValue.NewRawStr(s),
            long n => Core.Config.GitRawValue.NewRawInt(n),
            bool b => Core.Config.GitRawValue.NewRawBool(b),
            double => Core.Config.GitRawValue.NewRawBad("a float"),
            TomlTableArray => Core.Config.GitRawValue.NewRawBad("an array of tables"),
            TomlArray => Core.Config.GitRawValue.NewRawBad("an array"),
            TomlTable => Core.Config.GitRawValue.NewRawBad("a table"),
            _ => Core.Config.GitRawValue.NewRawBad("a value of another type"),
        };
    }

    // --- walker helpers ---------------------------------------------------

    private static TomlTable GetTable(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            throw new InvalidDataException($"Missing TOML section: [{key}]");
        if (value is not TomlTable sub)
            throw new InvalidDataException($"Expected [{key}] to be a table, got {value?.GetType().Name}");
        return sub;
    }

    private static int GetInt(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            throw new InvalidDataException($"Missing TOML key: {key}");
        return Convert.ToInt32(value);
    }

    private static double GetDouble(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            throw new InvalidDataException($"Missing TOML key: {key}");
        return Convert.ToDouble(value);
    }

    private static string GetString(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            throw new InvalidDataException($"Missing TOML key: {key}");
        return value as string
            ?? throw new InvalidDataException($"Expected TOML key {key} to be a string");
    }

    private static FSharpOption<double> TryGetDouble(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
            ? FSharpOption<double>.Some(Convert.ToDouble(value))
            : FSharpOption<double>.None;

    /// <summary>A TOML float or integer, else None (never throws).</summary>
    private static FSharpOption<double> TryGetNumberLenient(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
            ? value switch
            {
                double d => FSharpOption<double>.Some(d),
                long l => FSharpOption<double>.Some(l),
                _ => FSharpOption<double>.None,
            }
            : FSharpOption<double>.None;

    /// <summary>A TOML integer that fits an int, else None (never throws).</summary>
    private static FSharpOption<int> TryGetWholeNumberLenient(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is long l && l >= int.MinValue && l <= int.MaxValue
            ? FSharpOption<int>.Some((int)l)
            : FSharpOption<int>.None;

    private static FSharpOption<int> TryGetInt(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
            ? FSharpOption<int>.Some(Convert.ToInt32(value))
            : FSharpOption<int>.None;

    private static FSharpOption<bool> TryGetBool(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
            ? FSharpOption<bool>.Some(Convert.ToBoolean(value))
            : FSharpOption<bool>.None;

    private static FSharpOption<string> TryGetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string s
            ? FSharpOption<string>.Some(s)
            : FSharpOption<string>.None;

    private static FSharpOption<string[]> TryGetStringArray(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            return FSharpOption<string[]>.None;

        if (value is TomlArray tomlArray)
        {
            var arr = new string[tomlArray.Count];
            for (int i = 0; i < tomlArray.Count; i++)
            {
                if (tomlArray[i] is not string s)
                    throw new InvalidDataException($"Expected all elements in {key} array to be strings");
                arr[i] = s;
            }
            return FSharpOption<string[]>.Some(arr);
        }

        return FSharpOption<string[]>.None;
    }
}
