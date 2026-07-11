using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class PinCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public PinCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    private static (PinCommand cmd, FakeStore store, FakeVectorSearch vec, RecordingEmbedder emb) Build(
        int maxContentChars = 500)
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var emb = new RecordingEmbedder();
        return (new PinCommand(store, vec, emb, maxContentChars), store, vec, emb);
    }

    [Fact]
    public async Task MissingId_ReturnsExit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
        Assert.Contains("<id>", _errWriter.ToString());
    }

    [Fact]
    public async Task EntryNotFound_ReturnsExit1()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "nope" });
        Assert.Equal(1, code);
        Assert.Contains("not found", _errWriter.ToString());
    }

    [Fact]
    public async Task Pin_MovesToHot_MarksSticky_AndPrints()
    {
        // Tier model v2 (Task 9): pinning moves the entry into HOT and sets the
        // sticky flag (the retired pinned tier is gone).
        var (cmd, store, vec, emb) = Build();
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make("w1", "body"));

        var code = await cmd.RunAsync(new[] { "w1" });

        Assert.Equal(0, code);
        Assert.Single(store.MoveCalls);
        Assert.Equal((Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, "w1"), store.MoveCalls[0]);
        // Release gate: the entry ends up sticky-hot.
        Assert.True(store.IsSticky(ContentType.Memory, "w1"));
        Assert.Single(vec.Deletes);
        // w1 is seeded first → synthetic rowid 1 in FakeMemoryInfra.FakeStore.
        Assert.Equal((Tier.Warm, ContentType.Memory, 1L), vec.Deletes[0]);
        Assert.Single(emb.Calls);
        Assert.Equal("body", emb.Calls[0]);
        Assert.Single(vec.Inserts);
        Assert.Equal(Tier.Hot, vec.Inserts[0].Tier);
        Assert.Equal("w1", vec.Inserts[0].Id);
        Assert.Contains("pinned w1 (was warm/memory)", _outWriter.ToString());
    }

    [Fact]
    public async Task Pin_FreshPin_ResetsDecayScoreViaUpdate()
    {
        // Mirrors MemoryPinHandler: a fresh pin normalizes decay_score to 1.0
        // through a post-move Update against the HOT table.
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make("k1"));

        var code = await cmd.RunAsync(new[] { "k1" });

        Assert.Equal(0, code);
        Assert.True(store.IsSticky(ContentType.Knowledge, "k1"));
        var upd = Assert.Single(store.UpdateCalls);
        Assert.Equal(Tier.Hot, upd.Tier);
        Assert.Equal(ContentType.Knowledge, upd.Type);
        Assert.Equal("k1", upd.Id);
        Assert.Equal(1.0, upd.Opts.DecayScore);
        Assert.False(upd.Opts.ClearProject);
        Assert.Null(upd.Opts.Project);
    }

    [Fact]
    public async Task Pin_OverLimitContent_Exit2_NoMove()
    {
        var (cmd, store, _, _) = Build(maxContentChars: 10);
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make("w1", "this content is far too long"));

        var code = await cmd.RunAsync(new[] { "w1" });

        Assert.Equal(2, code);
        Assert.Empty(store.MoveCalls);
        Assert.Empty(store.UpdateCalls);
        Assert.Contains("limited to 10 characters", _errWriter.ToString());
    }

    [Fact]
    public async Task Pin_AlreadySticky_Exit0_NoMove()
    {
        // Idempotent: pinning an already-sticky hot entry succeeds without
        // moving, and with no scope requested it skips the Update write entirely.
        var (cmd, store, _, emb) = Build();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make("p1"));
        store.SetSticky(ContentType.Memory, "p1", true);

        var code = await cmd.RunAsync(new[] { "p1" });

        Assert.Equal(0, code);
        Assert.Empty(store.MoveCalls);
        Assert.Empty(emb.Calls);
        Assert.Empty(store.UpdateCalls);
        Assert.True(store.IsSticky(ContentType.Memory, "p1"));
        Assert.Contains("pinned p1 (was hot/memory)", _outWriter.ToString());
    }

    [Fact]
    public async Task Pin_AlreadySticky_ScopeStillApplied()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make("p1", project: "alpha"));
        store.SetSticky(ContentType.Memory, "p1", true);

        var code = await cmd.RunAsync(new[] { "p1", "--scope", "global" });

        Assert.Equal(0, code);
        Assert.Empty(store.MoveCalls);
        var upd = Assert.Single(store.UpdateCalls);
        Assert.Equal((Tier.Hot, ContentType.Memory, "p1"), (upd.Tier, upd.Type, upd.Id));
        Assert.True(upd.Opts.ClearProject);
        // Already sticky: decay score is left alone.
        Assert.Null(upd.Opts.DecayScore);
    }

    [Fact]
    public async Task Pin_ScopeGlobal_UpdatesWithClearProject()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make("w1", project: "alpha"));

        var code = await cmd.RunAsync(new[] { "w1", "--scope", "global" });

        Assert.Equal(0, code);
        Assert.Single(store.MoveCalls);
        var upd = Assert.Single(store.UpdateCalls);
        Assert.True(upd.Opts.ClearProject);
        Assert.Null(upd.Opts.Project);
        Assert.Equal(1.0, upd.Opts.DecayScore);
    }

    [Fact]
    public async Task Pin_ScopeProject_WithProjectFlag_UpdatesProject()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make("w1"));

        var code = await cmd.RunAsync(new[] { "w1", "--scope", "project", "--project", "alpha" });

        Assert.Equal(0, code);
        Assert.Single(store.MoveCalls);
        var upd = Assert.Single(store.UpdateCalls);
        Assert.Equal("alpha", upd.Opts.Project);
        Assert.False(upd.Opts.ClearProject);
    }

    [Fact]
    public async Task Pin_ScopeProject_FallsBackToEntryProject()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make("w1", project: "existing"));

        var code = await cmd.RunAsync(new[] { "w1", "--scope", "project" });

        Assert.Equal(0, code);
        var upd = Assert.Single(store.UpdateCalls);
        Assert.Equal("existing", upd.Opts.Project);
    }

    [Fact]
    public async Task Pin_ScopeProject_NoProjectAnywhere_Exit2_NoMove()
    {
        // The project-resolution failure must happen BEFORE the move so a
        // failed pin leaves the entry untouched.
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make("w1"));

        var code = await cmd.RunAsync(new[] { "w1", "--scope", "project" });

        Assert.Equal(2, code);
        Assert.Empty(store.MoveCalls);
        Assert.Empty(store.UpdateCalls);
        Assert.Contains("--scope project requires --project", _errWriter.ToString());
    }

    [Fact]
    public async Task Pin_InvalidScope_Exit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "w1", "--scope", "bogus" });
        Assert.Equal(2, code);
        Assert.Contains("invalid --scope", _errWriter.ToString());
    }

    [Fact]
    public async Task Pin_TypeOverride_MovesToKnowledge()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make("h1"));

        var code = await cmd.RunAsync(new[] { "h1", "--type", "knowledge" });

        Assert.Equal(0, code);
        Assert.Equal((Tier.Hot, ContentType.Memory, Tier.Hot, ContentType.Knowledge, "h1"), store.MoveCalls[0]);
        Assert.True(store.IsSticky(ContentType.Knowledge, "h1"));
    }

    [Fact]
    public async Task Pin_BadType_Exit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "w1", "--type", "nope" });
        Assert.Equal(2, code);
        Assert.Contains("invalid --type", _errWriter.ToString());
    }

    [Fact]
    public async Task UnknownArg_Exit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "w1", "--tier", "hot" });
        Assert.Equal(2, code);
        Assert.Contains("unknown argument", _errWriter.ToString());
    }
}
