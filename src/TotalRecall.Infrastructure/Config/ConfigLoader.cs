using System;
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
/// Loads <see cref="Core.Config.TotalRecallConfig"/> from TOML sources. Ports
/// <c>src-ts/config.ts</c> (<c>getDataDir</c>, <c>loadConfig</c>).
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
    /// Direct port of <c>getDataDir()</c> in <c>src-ts/config.ts</c>.
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
    /// Resolve the SQLite database file path. Port of <c>getDbPath()</c> in
    /// <c>src-ts/config.ts</c>.
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

        var trimmed = raw.Trim();

        if (trimmed.EndsWith('/') || trimmed.EndsWith('\\'))
        {
            throw new SqliteDbPathException(
                $"TOTAL_RECALL_DB_PATH must be a file path, not a directory. Got: \"{trimmed}\"");
        }

        if (trimmed == "~")
        {
            throw new SqliteDbPathException(
                "TOTAL_RECALL_DB_PATH must be a file path, not a directory. Got: \"~\"");
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
                $"TOTAL_RECALL_DB_PATH must be absolute or start with ~/. Got: \"{trimmed}\"");
        }

        return expanded;
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
            GetDouble(hot, "carry_forward_threshold"));

        var warmCfg = new Core.Config.WarmTierConfig(
            GetInt(warm, "max_entries"),
            GetInt(warm, "retrieval_top_k"),
            GetDouble(warm, "similarity_threshold"),
            GetInt(warm, "cold_decay_days"));

        var coldCfg = new Core.Config.ColdTierConfig(
            GetInt(cold, "chunk_max_tokens"),
            GetInt(cold, "chunk_overlap_tokens"),
            GetInt(cold, "lazy_summary_threshold"));

        var tiersCfg = new Core.Config.TiersConfig(hotCfg, warmCfg, coldCfg);

        var compaction = GetTable(table, "compaction");
        var compactionCfg = new Core.Config.CompactionConfig(
            GetDouble(compaction, "decay_half_life_hours"),
            GetDouble(compaction, "warm_threshold"),
            GetDouble(compaction, "promote_threshold"),
            GetInt(compaction, "warm_sweep_interval_days"));

        var embedding = GetTable(table, "embedding");
        var embeddingCfg = new Core.Config.EmbeddingConfig(
            GetString(embedding, "model"),
            GetInt(embedding, "dimensions"),
            TryGetString(embedding, "provider"),
            TryGetString(embedding, "endpoint"),
            TryGetString(embedding, "bedrock_region"),
            TryGetString(embedding, "bedrock_model"),
            TryGetString(embedding, "model_name"),
            TryGetString(embedding, "api_key"));

        FSharpOption<Core.Config.RegressionConfig> regression;
        if (table.TryGetValue("regression", out var regObj) && regObj is TomlTable regTable)
        {
            var regCfg = new Core.Config.RegressionConfig(
                TryGetDouble(regTable, "miss_rate_delta"),
                TryGetDouble(regTable, "latency_ratio"),
                TryGetInt(regTable, "min_events"));
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
        if (table.TryGetValue("skill", out var skillObj) && skillObj is TomlTable skillTable)
        {
            var extraDirs = TryGetStringArray(skillTable, "extra_dirs");
            var skillCfg = new Core.Config.SkillConfig(extraDirs);
            skill = FSharpOption<Core.Config.SkillConfig>.Some(skillCfg);
        }
        else
        {
            skill = FSharpOption<Core.Config.SkillConfig>.None;
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
            skill);
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

    private static FSharpOption<int> TryGetInt(TomlTable table, string key) =>
        table.TryGetValue(key, out var value)
            ? FSharpOption<int>.Some(Convert.ToInt32(value))
            : FSharpOption<int>.None;

    private static FSharpOption<string> TryGetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string s
            ? FSharpOption<string>.Some(s)
            : FSharpOption<string>.None;

    private static FSharpOption<string[]> TryGetStringArray(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            return FSharpOption<string[]>.None;

        if (value is System.Collections.IList list)
        {
            var arr = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not string s)
                    throw new InvalidDataException($"Expected all elements in {key} array to be strings");
                arr[i] = s;
            }
            return FSharpOption<string[]>.Some(arr);
        }

        return FSharpOption<string[]>.None;
    }
}
