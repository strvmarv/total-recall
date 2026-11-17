using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class LocalEntryReaderTests : IDisposable
{
    private readonly ScanRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private LocalRead Read(string id) =>
        LocalEntryReader.Read(
            tier => _rig.Store.Get(tier, ContentType.Memory, id),
            () => _rig.Store.IsSticky(ContentType.Memory, id));

    [Fact]
    public void NoEntry_IsAbsent() => Assert.IsType<LocalRead.Absent>(Read("3f9c2a7e"));

    [Theory]
    [InlineData("hot")]
    [InlineData("warm")]
    [InlineData("cold")]
    public void OneEntry_InAnyTier_IsPresentWithThatTier(string tierName)
    {
        var tier = tierName == "hot" ? Tier.Hot : tierName == "warm" ? Tier.Warm : Tier.Cold;
        _rig.AddMemory(tier, "3f9c2a7e", tags: ["b", "a"]);

        var read = Assert.IsType<LocalRead.Present>(Read("3f9c2a7e"));

        Assert.Equal(tier, read.Tier);
        Assert.Equal(["a", "b"], read.View.Tags.ToArray()); // normalised
        Assert.False(read.View.Sticky);
    }

    [Fact]
    public void AnIdInTwoTiers_IsDuplicate()
    {
        _rig.AddMemory(Tier.Hot, "3f9c2a7e");
        _rig.AddMemory(Tier.Cold, "3f9c2a7e");

        Assert.IsType<LocalRead.Duplicate>(Read("3f9c2a7e"));
    }

    [Fact]
    public void AGetterThatThrowsAMappingError_IsUnreadable_AndADatabaseErrorPropagates()
    {
        var unreadable = LocalEntryReader.Read(t => t.IsHot ? throw new FormatException("bad tags") : null, () => false);
        Assert.Contains("bad tags", Assert.IsType<LocalRead.Unreadable>(unreadable).Reason);

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            LocalEntryReader.Read(t => throw new Microsoft.Data.Sqlite.SqliteException("busy", 5), () => false));
    }

    [Fact]
    public void AnUnreadableRow_IsUnreadable()
    {
        _rig.AddMemory(Tier.Warm, "3f9c2a7e");
        _rig.Exec("UPDATE warm_memories SET source_tool = 'not-a-tool'");

        Assert.IsType<LocalRead.Unreadable>(Read("3f9c2a7e"));
    }

    [Fact]
    public void Sticky_IsReadForAHotEntryOnly()
    {
        _rig.AddMemory(Tier.Hot, "3f9c2a7e");
        _rig.Pin("3f9c2a7e");
        _rig.AddMemory(Tier.Warm, "aaaa1111");
        var asked = 0;

        Assert.True(Assert.IsType<LocalRead.Present>(Read("3f9c2a7e")).View.Sticky);
        LocalEntryReader.Read(t => _rig.Store.Get(t, ContentType.Memory, "aaaa1111"), () => { asked++; return true; });

        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheMapping_EqualsTheScansView_ForEmptyProjectAndScope()
    {
        _rig.AddMemory(Tier.Warm, "3f9c2a7e", project: "", scope: "", sourceTool: SourceTool.Cursor, entryType: EntryType.Decision);
        Assert.IsType<ScanOutcome.Done>(_rig.Scanner(1_700_000_000_000L).Scan());

        var view = Assert.IsType<LocalRead.Present>(Read("3f9c2a7e")).View;
        var row = _rig.Rows()["3f9c2a7e"];

        Assert.Equal(row.ScannedContentHash, SyncScan.contentHash(view.Content));
        Assert.Equal(row.ScannedTagsHash, SyncScan.tagsHash(view.Tags));
    }
}
