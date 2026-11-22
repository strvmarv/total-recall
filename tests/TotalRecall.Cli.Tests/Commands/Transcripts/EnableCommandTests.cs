using System;
using System.IO;
using TotalRecall.Cli.Commands.Transcripts;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class EnableCommandTests : IDisposable
{
    private readonly TranscriptsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private int Enable(params string[] args) => _h.Run(h => h.Enable(), args);

    private void AssertNothingWritten(string? configBefore = null)
    {
        Assert.Equal(configBefore, _h.ConfigText());
        Assert.False(File.Exists(_h.Sentinel.Path));
        Assert.False(_h.ArchiveExists());
    }

    [Fact]
    public void WithoutATerminal_ExitsTwo_AndWritesNothing()
    {
        _h.Interactive = false;

        var code = Enable();

        Assert.Equal(2, code);
        Assert.Contains("must be run in a terminal", _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        AssertNothingWritten();
        Assert.False(File.Exists(_h.DbPath));
        Assert.False(Directory.Exists(_h.DataDir));
    }

    [Fact]
    public void ExtraArguments_AreAUsageError()
    {
        var code = Enable("--yes");

        Assert.Equal(2, code);
        Assert.Contains("Usage: total-recall transcripts enable", _h.Err.ToString());
        AssertNothingWritten();
    }

    [Fact]
    public void ADeclinedConfirmation_ChangesNothing()
    {
        _h.Answer = false;

        var code = Enable();

        Assert.Equal(0, code);
        Assert.Single(_h.Prompts);
        AssertNothingWritten();
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void TheConfirmation_SaysWhatWillBeReadStoredAndEmbedded()
    {
        _h.Answer = false;

        Enable();

        var prompt = Assert.Single(_h.Prompts);
        Assert.Contains("dialogue", prompt);
        Assert.Contains("redacted", prompt);
        Assert.Contains("never synced", prompt);
        // Once on, the session files are read in the background at each session start.
        Assert.Contains("session files", prompt);
        Assert.Contains("in the background", prompt);
        Assert.Contains("each session start", prompt);
        // The text becomes vectors on this machine; a remote service only through the hand-set opt-in.
        Assert.Contains("vectors", prompt);
        Assert.Contains("on this machine", prompt);
        Assert.Contains("allow_remote_embedding", prompt);
        Assert.Contains("transcripts forget", prompt);
        Assert.DoesNotContain("Nothing is read until you run", prompt);
        Assert.DoesNotContain("cannot be searched yet", prompt);
        Assert.DoesNotContain("Nothing is stored yet", prompt);
        Assert.Contains(_h.DbPath, prompt);
    }

    [Theory]
    [InlineData("[storage]\nmode = \"postgres\"\nconnection_string = \"Host=h\"\n")]
    [InlineData("[storage]\nconnection_string = \"Host=h\"\n")]
    public void UnderPostgres_ExitsOne_WithTheReason_AndWritesNothing(string config)
    {
        _h.WriteConfig(config);

        var code = Enable();

        Assert.Equal(1, code);
        Assert.Contains("postgres", _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        AssertNothingWritten(config);
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void AnInvalidDbPath_ExitsOne_AndWritesNothing()
    {
        var config = "[transcripts]\ndb_path = \"relative/archive.db\"\n";
        _h.WriteConfig(config);

        var code = Enable();

        Assert.Equal(1, code);
        Assert.Contains("transcripts.db_path", _h.Err.ToString());
        Assert.Empty(_h.Prompts);
        AssertNothingWritten(config);
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void OnSuccess_TheTablesExist_TheSettingIsTrue_AndTheSentinelExists()
    {
        var code = Enable();

        Assert.Equal(0, code);
        Assert.True(_h.ArchiveExists());
        Assert.True(_h.ReadSettings().Enabled);
        Assert.Contains("enabled = true", _h.ConfigText());
        Assert.True(File.Exists(_h.Sentinel.Path));
        var output = _h.Out.ToString();
        Assert.Contains("enabled", output);
        Assert.Contains(_h.DbPath, output);
        Assert.Contains("From now on each session start archives your Claude Code sessions in the background. Run 'total-recall transcripts backfill' to archive the existing ones now.", output);
        Assert.DoesNotContain("arrives in a later release", output);
        Assert.DoesNotContain("Nothing is stored yet", output);
    }

    [Fact]
    public void ASeparateDbPath_IsCreated_AndNamed()
    {
        var archive = Path.Combine(_h.Root, "elsewhere", "archive.db");
        _h.WriteConfig($"[transcripts]\ndb_path = \"{archive.Replace("\\", "\\\\")}\"\n");

        var code = Enable();

        Assert.Equal(0, code);
        Assert.True(File.Exists(archive));
        Assert.False(File.Exists(_h.DbPath));
        Assert.Contains(archive, _h.Prompts[0]);
        Assert.Contains(archive, _h.Out.ToString());
        Assert.True(_h.ReadSettings().Enabled);
    }

    [Fact]
    public void WhenOnlyTheSentinelFails_ExitsOne_WithAMessage_AndTheSettingStaysTrue()
    {
        Directory.CreateDirectory(_h.DataDir);
        File.WriteAllText(Path.Combine(_h.DataDir, "live"), "a file where the directory should be");

        var code = Enable();

        Assert.Equal(1, code);
        var err = _h.Err.ToString();
        Assert.Contains(_h.Sentinel.Path, err);
        Assert.Contains("next engine start", err);
        Assert.True(_h.ReadSettings().Enabled);
        Assert.True(_h.ArchiveExists());
    }

    [Fact]
    public void WhenTheConfirmationThrows_ExitsOne_WithAMessage_AndChangesNothing()
    {
        _h.ConfirmThrows = new InvalidOperationException("no console");

        var code = Enable();

        Assert.Equal(1, code);
        Assert.Contains("no console", _h.Err.ToString());
        AssertNothingWritten();
    }

    [Fact]
    public void WhenTheConfigCannotBeWritten_ExitsOne_SaysEmptyTablesMayExist_AndWritesNoSentinel()
    {
        Directory.CreateDirectory(_h.ConfigPath); // a directory where config.toml should be

        var code = Enable();

        Assert.Equal(1, code);
        var err = _h.Err.ToString();
        Assert.Contains("could not write", err);
        Assert.Contains("empty archive tables may have been created", err);
        Assert.False(File.Exists(_h.Sentinel.Path));
        Assert.True(_h.ArchiveExists());
    }

    [Fact]
    public void AnOpenRefusal_ExitsOne_AndChangesNeitherSettingNorSentinel()
    {
        // a legacy (TypeScript-format) database: tables but no _schema_version
        Directory.CreateDirectory(_h.DataDir);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_h.DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE hot_memories (id TEXT PRIMARY KEY, content TEXT)";
            cmd.ExecuteNonQuery();
        }
        var config = "[transcripts]\nenabled = false\n";
        _h.WriteConfig(config);

        var code = Enable();

        Assert.Equal(1, code);
        Assert.Contains(LegacyRefusalFragment, _h.Err.ToString());
        Assert.Equal(config, _h.ConfigText());
        Assert.False(File.Exists(_h.Sentinel.Path));
    }

    private const string LegacyRefusalFragment = "start total-recall once";

    [Fact]
    public void AnUnresolvableDataDirectory_ExitsOne_AndCreatesNothing()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", null);
        Environment.SetEnvironmentVariable("HOME", null);
        Environment.SetEnvironmentVariable("USERPROFILE", null);
        var dataDir = ConfigLoader.GetDataDir();
        Assert.False(Path.IsPathRooted(dataDir));
        var relativeRoot = Path.GetFullPath("~");
        var existedBefore = Directory.Exists(relativeRoot);

        var code = _h.Run(h => h.Enable(
            mainDbPath: ConfigLoader.GetDbPath(),
            sentinel: new TranscriptSentinel(dataDir),
            configPath: Path.Combine(dataDir, "config.toml")));

        Assert.Equal(1, code);
        Assert.Contains("data directory", _h.Err.ToString());
        Assert.Equal(existedBefore, Directory.Exists(relativeRoot));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(dataDir, "config.toml"))));
        Assert.False(File.Exists(Path.GetFullPath(ConfigLoader.GetDbPath())));
        Assert.Empty(_h.Prompts);
    }

    [Fact]
    public void ADataDirectoryThatCannotBeWritten_ExitsOne_AndLeavesTheSettingUnchanged()
    {
        if (OperatingSystem.IsWindows())
            return; // the Unix permission bits are the test
        var config = "[transcripts]\nenabled = false\n";
        _h.WriteConfig(config);
        File.SetUnixFileMode(_h.DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var code = Enable();

        Assert.Equal(1, code);
        Assert.Equal(config, _h.ConfigText());
        Assert.False(_h.ReadSettings().Enabled);
        Assert.False(File.Exists(_h.Sentinel.Path));
        Assert.False(File.Exists(_h.DbPath));
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new EnableCommand();
        Assert.Equal("enable", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }
}
