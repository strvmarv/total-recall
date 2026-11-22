using System;
using System.Globalization;
using System.IO;
using TotalRecall.Cli.Commands.Transcripts;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Transcripts;

[Collection("ConsoleCapture")]
public sealed class StatusCommandTests : IDisposable
{
    private const string OldSearchLine = "search: not available in this version yet";

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
        Assert.DoesNotContain(OldSearchLine, _h.Out.ToString());
        Assert.DoesNotContain("search:", _h.Out.ToString());
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
        Assert.DoesNotContain(OldSearchLine, output);
        Assert.Contains("embedding: 0 of 3 chunks embedded (3 waiting)", output);
        Assert.Contains("search:    meaning and words", output);
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
    public void Status_NoLongerPrintsTheOldSearchLine()
    {
        _h.CreateArchive();
        _h.AddSession("claude-code", "s1");

        Assert.Equal(0, Status());
        Assert.DoesNotContain(OldSearchLine, _h.Out.ToString());
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
        Assert.Contains("could not read the archive", _h.Err.ToString());
        Assert.DoesNotContain(OldSearchLine, Output);
        Assert.DoesNotContain("embedding:", Output);
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
        Assert.DoesNotContain(OldSearchLine, Output);
    }

    [Fact]
    public void AMalformedConfig_ExitsOne_PrintsTheError_NoSettingNoNothingStored_NoOldSearchLine()
    {
        var archive = _h.CreateSeparateArchive();
        _h.WriteMalformedConfig(archive);

        var code = Status();

        Assert.Equal(1, code);
        Assert.Contains("could not read the configuration", _h.Err.ToString());
        var all = Output;
        Assert.DoesNotContain("nothing stored", all);
        Assert.DoesNotContain("enabled =", all);
        Assert.DoesNotContain(OldSearchLine, all);
        Assert.Equal((1L, 1L), _h.ArchiveCounts(archive));
    }

    // ---- the embedding lines ------------------------------------------------------------------------------------------

    private const string RemoteOff = "words only: the embedding provider is remote and allow_remote_embedding is off";

    private void EnableArchive(string extra = "")
    {
        _h.WriteConfig("[transcripts]\nenabled = true\n" + extra);
        _h.Sentinel.Write();
        _h.CreateArchive();
    }

    private void Remote(bool allow, int dims = 384)
    {
        EnableArchive($"allow_remote_embedding = {(allow ? "true" : "false")}\n[embedding]\nprovider = \"openai\"\n");
        _h.Embedder.Descriptor = new EmbedderDescriptor("openai", "text-embedding-3-small", "", dims);
    }

    private int RunEmbed() => WithSilentStderr(() => _h.Run(h => h.Embed()));

    private static int WithSilentStderr(Func<int> run)
    {
        var original = Console.Error;
        try
        {
            Console.SetError(new StringWriter());
            return run();
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private string[] Lines => _h.Out.ToString().Replace("\r", "").Split('\n');

    /// <summary>The five lines that follow <c>chunks: N</c>.</summary>
    private string[] EmbedBlock()
    {
        var lines = Lines;
        var at = Array.FindIndex(lines, l => l.StartsWith("chunks: ", StringComparison.Ordinal));
        Assert.True(at >= 0, _h.Out.ToString());
        return lines[(at + 1)..(at + 6)];
    }

    private string LastRunTime() =>
        _h.Time.GetUtcNow().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [Fact]
    public void ReadyWithVectors_PrintsTheFiveLinesAfterTheChunkCount()
    {
        EnableArchive();
        _h.SeedChunks(5);
        Assert.Equal(0, RunEmbed());

        Assert.Equal(0, Status());

        Assert.Equal(
            [
                "embedding: 5 of 5 chunks embedded (0 waiting)",
                "embedder:  local bge-small-en-v1.5 (ready)",
                "vectors:   made by local bge-small-en-v1.5",
                $"last embed run: {LastRunTime()}, embedded 5, 0 remaining, finished",
                "search:    meaning and words",
            ],
            EmbedBlock());
        Assert.Equal(2, _h.Embedder.Disposed); // the embed run's and the status's
    }

    [Fact]
    public void NothingEmbeddedYet()
    {
        EnableArchive();
        _h.SeedChunks(2);

        Assert.Equal(0, Status());

        Assert.Equal(
            [
                "embedding: 0 of 2 chunks embedded (2 waiting)",
                "embedder:  local bge-small-en-v1.5 (ready)",
                "vectors:   none yet",
                "last embed run: none",
                "search:    meaning and words",
            ],
            EmbedBlock());
    }

    [Fact]
    public void CouldNotBeEmbedded_IsCounted()
    {
        EnableArchive();
        _h.SeedChunks(4);
        using (var conn = _h.Connect())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tx_chunk SET embed_attempts = 3 WHERE content = 'chunk-2'";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(0, Status());
        Assert.Equal("embedding: 0 of 4 chunks embedded (3 waiting, 1 could not be embedded)", EmbedBlock()[0]);
    }

    [Fact]
    public void RemoteWithoutOptIn_WordsOnly_NothingInspected()
    {
        Remote(allow: false);
        _h.SeedChunks(2);
        _h.Inspector = cfg => EmbedderInspection.Inspect(cfg); // would print a stack trace if it ran

        Assert.Equal(0, Status());

        var block = EmbedBlock();
        Assert.Equal("embedder:  " + RemoteOff, block[1]);
        Assert.Equal("vectors:   none yet", block[2]);
        Assert.Equal("search:    words only", block[4]);
        Assert.Empty(_h.Inspections);
    }

    [Fact]
    public void Dimension_WordsOnly()
    {
        Remote(allow: true, dims: 768);
        _h.SeedChunks(2);

        Assert.Equal(0, Status());

        var block = EmbedBlock();
        Assert.Equal("embedder:  words only: the provider's vectors have 768 dimensions; the archive holds 384", block[1]);
        Assert.Equal("search:    words only", block[4]);
    }

    [Fact]
    public void ModelNotReady_WordsOnly()
    {
        EnableArchive();
        _h.SeedChunks(2);
        _h.Readiness = new EmbedderReadiness(false, "model.onnx is missing");

        Assert.Equal(0, Status());

        var block = EmbedBlock();
        Assert.Equal("embedder:  words only: embedding model not ready (model.onnx is missing)", block[1]);
        Assert.Equal("search:    words only", block[4]);
    }

    [Fact]
    public void VectorsMadeByAnotherModel_NotUsed_WordsOnly()
    {
        EnableArchive();
        _h.SeedChunks(2);
        Assert.Equal(0, RunEmbed());
        _h.Embedder = new FakeEmbedder { Descriptor = new EmbedderDescriptor("local", "other-model", "r", 384) };

        Assert.Equal(0, Status());

        var block = EmbedBlock();
        Assert.Equal(
            "embedder:  words only: vectors were made by local/bge-small-en-v1.5; the running embedder is local/other-model",
            block[1]);
        Assert.Equal("vectors:   made by local bge-small-en-v1.5 (not used)", block[2]);
        Assert.Equal("search:    words only", block[4]);
    }

    [Fact]
    public void ThrowingFactory_PrintsTheNotReadyLine()
    {
        Remote(allow: true); // no endpoint: the real factory throws
        _h.SeedChunks(2);
        _h.Inspector = cfg => EmbedderInspection.Inspect(cfg);

        Assert.Equal(0, WithSilentStderr(() => Status()));

        var block = EmbedBlock();
        Assert.Equal(
            "embedder:  words only: embedding model not ready (embedding.endpoint required when provider = 'openai')",
            block[1]);
        Assert.Equal("search:    words only", block[4]);
    }

    [Fact]
    public void ThrowingFactory_WithoutOptIn_PrintsTheRemoteLine()
    {
        Remote(allow: false);
        _h.SeedChunks(2);
        _h.Inspector = cfg => EmbedderInspection.Inspect(cfg);

        Assert.Equal(0, Status());

        Assert.Equal("embedder:  " + RemoteOff, EmbedBlock()[1]);
        Assert.Empty(_h.Inspections);
    }

    [Fact]
    public void ArchiveOff_WithData_NotApplicable_NothingInspected()
    {
        _h.CreateArchive();
        _h.SeedChunks(2);

        Assert.Equal(0, Status());

        var block = EmbedBlock();
        Assert.Equal("embedder:  not applicable (the archive is off)", block[1]);
        Assert.Equal("search:    not available (the archive is off)", block[4]);
        Assert.Empty(_h.Inspections);
    }

    [Fact]
    public void LastRun_StoppedByAFailure_PrintsTheRecordedState()
    {
        EnableArchive();
        _h.SeedChunks(2);
        _h.Embedder.OnEmbed = _ => throw new IOException("the disk went away");
        Assert.Equal(1, RunEmbed());
        _h.Embedder.OnEmbed = null;

        Assert.Equal(0, Status());

        Assert.Equal(
            $"last embed run: {LastRunTime()}, embedded 0, 2 remaining, stopped: embedding failed: the disk went away",
            EmbedBlock()[3]);
    }

    [Fact]
    public void LastRun_StoppedByTheGate_PrintsTheRecordedState()
    {
        EnableArchive();
        _h.SeedChunks(2);
        _h.Embedder.OnEmbed = _ => throw new InvalidOperationException("model.onnx not found");
        Assert.Equal(1, RunEmbed());

        Assert.Equal(0, Status());

        Assert.Equal(
            $"last embed run: {LastRunTime()}, embedded 0, 2 remaining, " +
            "stopped: words only: embedding model not ready (model.onnx not found)",
            EmbedBlock()[3]);
    }

    [Fact]
    public void Status_ProbeDimensionRefusal_ShowsWordsOnly()
    {
        Remote(allow: true);
        _h.Embedder.Length = 1536; // the configured 384 is ignored by the service
        _h.SeedChunks(2);
        Assert.Equal(1, RunEmbed());

        Assert.Equal(0, Status());

        const string refused = "words only: the provider's vectors have 1536 dimensions; the archive holds 384";
        var block = EmbedBlock();
        Assert.Equal("embedder:  " + refused, block[1]);
        Assert.Equal($"last embed run: {LastRunTime()}, embedded 0, 2 remaining, stopped: {refused}", block[3]);
        Assert.Equal("search:    words only", block[4]);
    }

    [Fact]
    public void Status_DimensionRefusalOverride_ClearsWhenConfigChanges()
    {
        Remote(allow: true);
        _h.Embedder.Length = 1536;
        _h.SeedChunks(2);
        Assert.Equal(1, RunEmbed());

        // another model now: the recorded refusal was the old model's
        _h.Embedder = new FakeEmbedder { Descriptor = new EmbedderDescriptor("openai", "text-embedding-3-large", "", 384) };

        Assert.Equal(0, Status());

        var block = EmbedBlock();
        Assert.Equal("embedder:  openai text-embedding-3-large (ready)", block[1]);
        Assert.Equal("search:    meaning and words", block[4]);

        // and back to the refused model: the override applies again
        _h.Embedder = new FakeEmbedder { Descriptor = new EmbedderDescriptor("openai", "text-embedding-3-small", "", 384) };
        Assert.Equal(0, Status());
        Assert.Equal("search:    words only", EmbedBlock()[4]);
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
