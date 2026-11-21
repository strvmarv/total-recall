using System;
using System.IO;
using TotalRecall.Cli.Commands.Transcripts;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class StatusCommandTests : IDisposable
{
    private const string SearchLine = "search: not available in this version yet";

    private readonly TranscriptsHarness _h = new();

    public StatusCommandTests() => _h.Interactive = false; // status never needs a terminal

    public void Dispose() => _h.Dispose();

    private int Status(params string[] args) => _h.Run(h => h.Status(), args);

    private string Output => _h.Out.ToString() + _h.Err.ToString();

    [Fact]
    public void WithNoDatabase_SaysOffNothingStored_AndCreatesNoFile()
    {
        var code = Status();

        Assert.Equal(0, code);
        Assert.Contains("enabled = false", _h.Out.ToString());
        Assert.Contains("off, nothing stored", _h.Out.ToString());
        Assert.Contains(SearchLine, _h.Out.ToString());
        Assert.False(File.Exists(_h.DbPath));
        Assert.False(Directory.Exists(_h.DataDir));
        Assert.Empty(_h.Prompts);
    }

    [Fact]
    public void WithAMainDatabaseButNoArchiveTables_SaysNothingStored_AndAddsNoTables()
    {
        Directory.CreateDirectory(_h.DataDir);
        using (var conn = _h.Connect())
            TotalRecall.Infrastructure.Storage.MigrationRunner.RunMigrations(conn);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var code = Status();

        Assert.Equal(0, code);
        Assert.Contains("nothing stored", _h.Out.ToString());
        Assert.False(_h.ArchiveExists());
    }

    [Fact]
    public void WithData_PrintsTheCounts_TheForgottenRecords_AndTheCutoff()
    {
        _h.CreateArchive();
        using (var store = _h.OpenArchive())
        {
            store.ForgetSession("claude-code", "gone-session");
            store.ForgetProject("owner/old-project");
            store.ForgetAll(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var a = _h.AddSession("claude-code", "s1");
        var b = _h.AddSession("pi", "s2", kind: "automated");
        _h.AddSession("pi", "s3");
        _h.AddChunk(a, "one");
        _h.AddChunk(a, "two", exchangeStart: 1);
        _h.AddChunk(b, "three");
        _h.WriteConfig("[transcripts]\nenabled = true\n");
        _h.Sentinel.Write();

        var code = Status();

        Assert.Equal(0, code);
        var output = _h.Out.ToString();
        Assert.Contains("enabled = true", output);
        Assert.Contains("sessions: 3 (interactive 2, automated 1)", output);
        Assert.Contains("chunks: 3", output);
        Assert.Contains("claude-code:gone-session", output);
        Assert.Contains("owner/old-project", output);
        Assert.Contains("cutoff: 2026-01-02T03:04:05", output);
        Assert.Contains("main database (" + _h.DbPath + ")", output);
        Assert.Contains(SearchLine, output);
        Assert.DoesNotContain("nothing stored", output);
        Assert.DoesNotContain("differ", output);
    }

    [Fact]
    public void Status_PrintsProjectKindRows()
    {
        _h.CreateArchive();
        var a = _h.AddSession("claude-code", "s1", projectKey: "owner/repo");
        var b = _h.AddSession("claude-code", "s2", projectKey: "owner/repo", kind: "automated");
        var c = _h.AddSession("claude-code", "s3", projectKey: "other");
        var d = _h.AddSession("claude-code", "s4", projectKey: "other");
        _h.AddChunk(a, "one");
        _h.AddChunk(a, "two", exchangeStart: 1);
        _h.AddChunk(a, "three", exchangeStart: 2);
        _h.AddChunk(b, "four");
        _h.AddChunk(c, "five");
        _h.AddChunk(d, "six");

        var code = Status();

        Assert.Equal(0, code);
        var lines = _h.Out.ToString().Replace("\r", "").Split('\n');
        var at = Array.IndexOf(lines, "by project and kind:");
        Assert.True(at > Array.FindIndex(lines, l => l.StartsWith("chunks:", StringComparison.Ordinal)));
        Assert.Equal("  owner/repo interactive: 1 sessions, 3 chunks", lines[at + 1]);
        Assert.Equal("  other interactive: 2 sessions, 2 chunks", lines[at + 2]);
        Assert.Equal("  owner/repo automated: 1 sessions, 1 chunks", lines[at + 3]);
    }

    [Fact]
    public void Status_NoRows_PrintsNoProjectSection()
    {
        _h.CreateArchive();

        Assert.Equal(0, Status());
        Assert.DoesNotContain("by project and kind:", _h.Out.ToString());
    }

    [Fact]
    public void Status_MoreThan30Rows_PrintsAndNMore()
    {
        _h.CreateArchive();
        for (var i = 0; i < 31; i++)
            _h.AddSession("claude-code", $"s{i:D2}", projectKey: $"p{i:D2}");

        var code = Status();

        Assert.Equal(0, code);
        var lines = _h.Out.ToString().Replace("\r", "").Split('\n');
        var rows = Array.FindAll(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^  p\d\d interactive: "));
        Assert.Equal(30, rows.Length);
        Assert.Contains("  and 1 more", lines);
    }

    [Fact]
    public void Status_KeepsSearchNotAvailableLine()
    {
        _h.CreateArchive();
        _h.AddSession("claude-code", "s1");

        Assert.Equal(0, Status());
        Assert.Contains(SearchLine, _h.Out.ToString());
    }

    [Fact]
    public void ADisabledArchiveThatStillHoldsData_ShowsItsCounts()
    {
        _h.CreateArchive();
        _h.AddSession("pi", "s1");

        var code = Status();

        Assert.Equal(0, code);
        Assert.Contains("enabled = false", _h.Out.ToString());
        Assert.Contains("sessions: 1", _h.Out.ToString());
    }

    [Fact]
    public void ASettingWithoutTheSentinel_IsReportedAsAMismatch()
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n");

        var code = Status();

        Assert.Equal(0, code);
        Assert.Contains("differ", _h.Out.ToString());
        Assert.Contains("next engine start", _h.Out.ToString());
    }

    [Fact]
    public void ASentinelWithoutTheSetting_IsReportedAsAMismatch()
    {
        _h.Sentinel.Write();

        var code = Status();

        Assert.Equal(0, code);
        Assert.Contains("differ", _h.Out.ToString());
        Assert.Contains("next engine start", _h.Out.ToString());
    }

    [Fact]
    public void AnUnreadableDatabase_ExitsOne_AndDoesNotSayNothingStored()
    {
        Directory.CreateDirectory(_h.DataDir);
        File.WriteAllText(_h.DbPath, "this is not a database, it only has the name of one. ".PadRight(4096, 'x'));

        var code = Status();

        Assert.Equal(1, code);
        Assert.DoesNotContain("nothing stored", Output);
        Assert.Contains(SearchLine, _h.Out.ToString());
    }

    [Fact]
    public void ALockedDatabase_WithAPendingLog_ExitsOne_AndDoesNotSayNothingStored()
    {
        _h.CreateArchive();
        File.WriteAllBytes(_h.DbPath + "-wal", new byte[64]);

        var code = Status();

        Assert.Equal(1, code);
        Assert.DoesNotContain("nothing stored", Output);
    }

    [Fact]
    public void AnInvalidDbPath_IsExplained_AndExitsOne()
    {
        _h.WriteConfig("[transcripts]\ndb_path = \"relative.db\"\n");

        var code = Status();

        Assert.Equal(1, code);
        Assert.Contains("transcripts.db_path", Output);
        Assert.DoesNotContain("nothing stored", Output);
        Assert.Contains(SearchLine, _h.Out.ToString());
    }

    [Fact]
    public void AMalformedConfig_ExitsOne_PrintsTheError_NoSettingNoNothingStored_AndTheSearchLine()
    {
        var archive = _h.CreateSeparateArchive();
        _h.WriteMalformedConfig(archive);

        var code = Status();

        Assert.Equal(1, code);
        Assert.Contains("could not read the configuration", _h.Err.ToString());
        var all = Output;
        Assert.DoesNotContain("nothing stored", all);
        Assert.DoesNotContain("enabled =", all);
        Assert.Contains(SearchLine, _h.Out.ToString());
        Assert.Equal((1L, 1L), _h.ArchiveCounts(archive));
    }

    [Fact]
    public void ExtraArguments_AreAUsageError()
    {
        Assert.Equal(2, Status("--json"));
    }

    [Fact]
    public void TheCommand_IsInTheTranscriptsGroup_AndVisible()
    {
        var command = new StatusCommand();
        Assert.Equal("status", command.Name);
        Assert.Equal("transcripts", command.Group);
        Assert.False(command.Hidden);
    }
}
