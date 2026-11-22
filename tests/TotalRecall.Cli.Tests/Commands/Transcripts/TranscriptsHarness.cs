using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TotalRecall.Cli.Commands.Transcripts;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using EmbeddingConfig = TotalRecall.Core.Config.EmbeddingConfig;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SqliteOpener = TotalRecall.Infrastructure.Storage.SqliteConnection;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

/// <summary>
/// A temporary tree for the <c>transcripts</c> command tests: the data directory, the database path, HOME and
/// USERPROFILE are redirected into it for the life of a test (the classes run in the ConsoleCapture collection, so the
/// process-wide variables are never shared), and the commands are built with seams that read the real files of that
/// tree. Nothing ever touches the real <c>~/.total-recall</c>.
/// </summary>
internal sealed class TranscriptsHarness : IDisposable
{
    private static readonly string[] Variables = ["TOTAL_RECALL_HOME", "TOTAL_RECALL_DB_PATH", "HOME", "USERPROFILE"];

    private readonly Dictionary<string, string?> _saved = [];

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tr-cli-transcripts-" + Guid.NewGuid().ToString("N"));
    public string DataDir => Path.Combine(Root, "home");
    public string ConfigPath => Path.Combine(DataDir, "config.toml");
    public string DbPath => Path.Combine(DataDir, "total-recall.db");
    public TranscriptSentinel Sentinel => new(DataDir);

    public bool Interactive { get; set; } = true;
    public bool Answer { get; set; } = true;
    public List<string> Prompts { get; } = [];

    /// <summary>What the command had written to its output when it asked (one entry per prompt).</summary>
    public List<string> OutputAtPrompt { get; } = [];

    public StringWriter Out { get; private set; } = new();
    public StringWriter Err { get; private set; } = new();

    public TranscriptsHarness()
    {
        foreach (var v in Variables)
            _saved[v] = Environment.GetEnvironmentVariable(v);
        Directory.CreateDirectory(Root);
        var home = Path.Combine(Root, "userhome");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", DataDir);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", null);
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("USERPROFILE", home);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        MsSqliteConnection.ClearAllPools();
        try
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(DataDir))
                File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    // ---- building the commands ------------------------------------------------------------------------------------

    public TranscriptsSettings ReadSettings() => ConfigLoader.LoadTranscriptsSettings(ConfigPath);

    public string ReadStorageMode() => TranscriptSentinel.EffectiveStorageMode(new ConfigLoader().LoadEffectiveConfig(ConfigPath));

    /// <summary>When set, the confirmation seam throws this (a production prompt that fails).</summary>
    public Exception? ConfirmThrows { get; set; }

    private bool Confirm(string prompt)
    {
        if (ConfirmThrows is not null)
            throw ConfirmThrows;
        Prompts.Add(prompt);
        OutputAtPrompt.Add(Out.ToString());
        return Answer;
    }

    public EnableCommand Enable(string? mainDbPath = null, TranscriptSentinel? sentinel = null, string? configPath = null) =>
        new(ReadSettings, mainDbPath ?? DbPath, ReadStorageMode, sentinel ?? Sentinel, () => Interactive, Confirm, Out, Err,
            configPath ?? ConfigPath);

    public DisableCommand Disable() =>
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath);

    public StatusCommand Status() =>
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath,
            ReadEmbeddingConfig, InspectSeam);

    public BackfillCommand Backfill(Func<string, ITranscriptAdapter> adapterFactory, CancellationToken cancellation = default) =>
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath,
            adapterFactory, cancellation, ReadEmbeddingConfig, InspectSeam, Time);

    public EmbedCommand Embed(CancellationToken? cancellation = null) =>
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath,
            ReadEmbeddingConfig, InspectSeam, Time, cancellation ?? CancellationToken.None);

    // ---- the embedder seam ----------------------------------------------------------------------------------------

    /// <summary>The embedder the default inspector hands out (local bge-small, 384 floats, ready).</summary>
    public FakeEmbedder Embedder { get; set; } = new();

    /// <summary>The readiness the default inspector reports.</summary>
    public EmbedderReadiness Readiness { get; set; } = new(true, null);

    /// <summary>Replaces the default inspector (for example with the real <see cref="EmbedderInspection.Inspect"/>).</summary>
    public Func<EmbeddingConfig, EmbedderInspectionResult>? Inspector { get; set; }

    /// <summary>Every embedding config the commands inspected.</summary>
    public List<EmbeddingConfig> Inspections { get; } = [];

    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));

    public EmbeddingConfig ReadEmbeddingConfig() => new ConfigLoader().LoadEffectiveConfig(ConfigPath).Embedding;

    private EmbedderInspectionResult InspectSeam(EmbeddingConfig cfg)
    {
        Inspections.Add(cfg);
        if (Inspector is not null)
            return Inspector(cfg);
        var d = Embedder.Descriptor;
        return new EmbedderInspectionResult(
            Embedder, new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions), Readiness, null);
    }

    public ForgetCommand Forget() =>
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath);

    public UnforgetCommand Unforget() =>
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath);

    /// <summary>Clears the captured output, builds the command (so it gets the fresh writers) and runs it.</summary>
    public int Run(Func<TranscriptsHarness, ICliCommand> build, params string[] args)
    {
        Out = new StringWriter();
        Err = new StringWriter();
        Prompts.Clear();
        OutputAtPrompt.Clear();
        return build(this).RunAsync(args).GetAwaiter().GetResult();
    }

    // ---- files ----------------------------------------------------------------------------------------------------

    public void WriteConfig(string toml)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, toml);
    }

    public string? ConfigText() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;

    /// <summary>Creates the main database with the archive tables (as <c>enable</c> would) and closes it.</summary>
    public void CreateArchive()
    {
        Directory.CreateDirectory(DataDir);
        var result = TranscriptStore.Open(new TranscriptLocation(DbPath, IsMainDatabase: true), create: true);
        if (result.Status != TranscriptOpenStatus.Opened || result.Store is null)
            throw new InvalidOperationException($"could not create the test archive: {result.Status} {result.Message}");
        result.Store.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Creates a separate archive file (a <c>transcripts.db_path</c> target) holding one session and one chunk, and
    /// returns its path; its row counts are read with <see cref="ArchiveCounts"/>.
    /// </summary>
    public string CreateSeparateArchive(string name = "archive.db")
    {
        Directory.CreateDirectory(DataDir);
        var path = Path.Combine(DataDir, name);
        var result = TranscriptStore.Open(new TranscriptLocation(path, IsMainDatabase: false), create: true);
        if (result.Status != TranscriptOpenStatus.Opened || result.Store is null)
            throw new InvalidOperationException($"could not create the test archive: {result.Status} {result.Message}");
        result.Store.Dispose();
        MsSqliteConnection.ClearAllPools();
        using (var conn = SqliteOpener.Open(path, pooling: false))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) " +
                "VALUES ('claude-code', 's1', 'owner/repo', 'remote', 'interactive'); " +
                "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) " +
                "VALUES (last_insert_rowid(), 0, 0, 0, 'dialogue', 1, 'precious');";
            cmd.ExecuteNonQuery();
        }
        MsSqliteConnection.ClearAllPools();
        return path;
    }

    public (long Sessions, long Chunks) ArchiveCounts(string path)
    {
        using var conn = SqliteOpener.Open(path, pooling: false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT count(*) FROM tx_session), (SELECT count(*) FROM tx_chunk)";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    /// <summary>A config.toml that selects <paramref name="archivePath"/> and is not valid TOML.</summary>
    public void WriteMalformedConfig(string archivePath) =>
        WriteConfig($"[transcripts]\nenabled = true\ndb_path = \"{archivePath.Replace("\\", "\\\\")}\"\n[broken\nthis is = = not toml\n");

    /// <summary>Opens the existing archive in the main database (never creates it).</summary>
    public ITranscriptStore OpenArchive()
    {
        var result = TranscriptStore.Open(new TranscriptLocation(DbPath, IsMainDatabase: true), create: false);
        if (result.Status != TranscriptOpenStatus.Opened || result.Store is null)
            throw new InvalidOperationException($"no archive: {result.Status} {result.Message}");
        return result.Store;
    }

    public bool ArchiveExists()
    {
        var result = TranscriptStore.Open(new TranscriptLocation(DbPath, IsMainDatabase: true), create: false);
        result.Store?.Dispose();
        MsSqliteConnection.ClearAllPools();
        return result.Status == TranscriptOpenStatus.Opened;
    }

    public MsSqliteConnection Connect() => SqliteOpener.Open(DbPath, pooling: false);

    public long AddSession(string host, string hostSessionId, string projectKey = "owner/repo", string kind = "interactive",
        string? cwd = null)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, cwd, kind) " +
            "VALUES ($h, $s, $p, 'remote', $c, $k); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$s", hostSessionId);
        cmd.Parameters.AddWithValue("$p", projectKey);
        cmd.Parameters.AddWithValue("$c", (object?)cwd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", kind);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void AddChunk(long sessionId, string content, int exchangeStart = 0)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, redaction_version, content) " +
            "VALUES ($s, $e, $e, 0, 'dialogue', 1, $c)";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$e", exchangeStart);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>A session with <paramref name="count"/> unembedded chunks whose content is <c>chunk-&lt;i&gt;</c>.</summary>
    public void SeedChunks(int count, string hostSessionId = "seed", string prefix = "chunk-")
    {
        var session = AddSession("claude-code", hostSessionId);
        for (var i = 0; i < count; i++)
            AddChunk(session, prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture), exchangeStart: i);
    }
}

/// <summary>A deterministic embedder: 384 floats by default, records every text, optional hooks per text.</summary>
internal sealed class FakeEmbedder : IEmbedder, IDisposable
{
    public EmbedderDescriptor Descriptor { get; set; } = new("local", "bge-small-en-v1.5", "rev1", 384);

    /// <summary>The length of the returned vectors (independent of the descriptor, to fake a lying remote).</summary>
    public int Length { get; set; } = 384;

    public List<string> Texts { get; } = [];

    /// <summary>Runs before each embed; may throw to fail the text.</summary>
    public Action<string>? OnEmbed { get; set; }

    public int Disposed { get; private set; }

    public float[] Embed(string text)
    {
        Texts.Add(text);
        OnEmbed?.Invoke(text);
        var v = new float[Length];
        var seed = text.Length + 1;
        for (var i = 0; i < v.Length; i++)
            v[i] = (float)Math.Sin(seed * (i + 1));
        return v;
    }

    public void Dispose() => Disposed++;
}

/// <summary>A clock that moves only when told to.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTime(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
