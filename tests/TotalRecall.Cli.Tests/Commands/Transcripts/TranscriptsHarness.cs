using System;
using System.Collections.Generic;
using System.IO;
using TotalRecall.Cli.Commands.Transcripts;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;
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

    private bool Confirm(string prompt)
    {
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
        new(ReadSettings, DbPath, ReadStorageMode, Sentinel, () => Interactive, Confirm, Out, Err, ConfigPath);

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
}
