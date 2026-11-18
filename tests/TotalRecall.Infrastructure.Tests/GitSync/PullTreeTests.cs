using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class PullTreeTests : IDisposable
{
    private const string GoodId = "3f9c2a7e-0000-4000-8000-000000000000";
    private const string SymlinkId = "3f9d2a7e-0000-4000-8000-000000000000";
    private const string SubmoduleId = "3f000000-0000-4000-8000-0000000000aa";
    private const string BigId = "3f111111-0000-4000-8000-000000000000";

    private readonly PullRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private async Task<TreeView> Read(string head, string? last = null, bool unproven = true)
    {
        var read = await PullTree.ReadAsync(_rig.Client, head, last, unproven, CancellationToken.None);
        return Assert.IsType<TreeRead.Ok>(read).View;
    }

    /// <summary>One commit holding every shape the repository can hold, cloned.</summary>
    private string EveryShape()
    {
        _rig.PutManifest();
        _rig.PutText($"memories/3f/{GoodId}.json", "{}");
        _rig.PutRaw($"memories/3f/{SymlinkId}.json", Bytes("../../etc/passwd"), "120000");
        _rig.PutRaw($"memories/3f/{SubmoduleId}.json", Bytes(""), "100644");
        _rig.Source.Add("160000", new string('7', 40), $"memories/3f/{SubmoduleId}.json");
        _rig.PutText($"memories/4f/{GoodId}.json", "{}");            // wrong shard
        _rig.PutText("memories/ab/..%2f..%2fx.json", "{}");          // not an id
        _rig.PutText("memories/ab/ab.json", "{}");                   // too short an id
        _rig.PutRaw($"memories/3f/{BigId}.json", new byte[1_048_577]); // over 1 MiB
        _rig.PutText($"memories/3f/sub/{GoodId}.json", "{}");        // nested
        var head = _rig.Commit("every shape");
        _rig.Clone();
        return head;
    }

    [GitFact]
    public async Task EveryShape_IsClassified_AcceptedOrRejectedWithTheReasonWord()
    {
        var view = await Read(EveryShape());

        Assert.Equal([GoodId], view.Accepted.Keys.ToArray());
        Assert.Equal($"memories/3f/{GoodId}.json", view.Accepted[GoodId].Path);
        var byReason = view.Rejected.ToDictionary(r => r.Path, r => r.Reason);
        Assert.Equal("symlink", byReason[$"memories/3f/{SymlinkId}.json"]);
        Assert.Equal("submodule", byReason[$"memories/3f/{SubmoduleId}.json"]);
        Assert.Equal("bad-shard", byReason[$"memories/4f/{GoodId}.json"]);
        Assert.Equal("too-large", byReason[$"memories/3f/{BigId}.json"]);
        Assert.Equal("bad-id", byReason["memories/ab/ab.json"]);
        Assert.Equal("bad-path", byReason[$"memories/3f/sub/{GoodId}.json"]);
        Assert.DoesNotContain(view.Rejected, r => r.Path == "manifest.json"); // the listing is limited to memories/
    }

    [GitFact]
    public async Task ARejectedPathOfAKnownId_IsPresent_NeverAccepted_SoItIsNeverDecidedAsDeleted()
    {
        var view = await Read(EveryShape());

        foreach (var id in new[] { SymlinkId, SubmoduleId, BigId })
        {
            Assert.Contains(id, view.Present);
            Assert.DoesNotContain(id, view.Accepted.Keys);
            Assert.Contains(view.Rejected, r => r.NamesId == id);
        }
    }

    [GitFact]
    public async Task APathThatNamesNoId_IsRejectedAndNamesNone()
    {
        var view = await Read(EveryShape());

        foreach (var path in new[] { $"memories/4f/{GoodId}.json", "memories/ab/..%2f..%2fx.json", "memories/ab/ab.json", $"memories/3f/sub/{GoodId}.json" })
            Assert.Null(view.Rejected.Single(r => r.Path == path).NamesId);
        Assert.Equal(4, view.Present.Count); // the good id and the three that name one
    }

    [GitFact]
    public async Task TheRejectionKey_IsTheSanitisedPath_CutAt64()
    {
        var long64 = "memories/ab/" + new string('x', 80) + ".json";
        _rig.PutManifest();
        _rig.PutText(long64, "{}");
        var head = _rig.Commit();
        _rig.Clone();

        var rejected = Assert.Single((await Read(head)).Rejected);

        Assert.Equal(SyncApply.rejectionKey(long64), rejected.Key);
        Assert.Equal(64, rejected.Key.Length);
    }

    [GitFact]
    public async Task Changed_ComesFromTheDiff_OnlyWhenProven_AndNeverIncludesDeletedOrRejectedIds()
    {
        const string a = "3f9c2a7e-0000-4000-8000-0000000000a1";
        const string b = "3f9c2a7e-0000-4000-8000-0000000000b2";
        const string c = "3f9c2a7e-0000-4000-8000-0000000000c3";
        _rig.PutManifest();
        _rig.PutText($"memories/3f/{a}.json", "{\"v\":1}");
        _rig.PutText($"memories/3f/{c}.json", "{\"v\":1}");
        var first = _rig.Commit("first");
        _rig.Clone();
        _rig.PutText($"memories/3f/{a}.json", "{\"v\":2}");                  // modified
        _rig.PutText($"memories/3f/{b}.json", "{\"v\":1}");                  // added
        _rig.Remove($"memories/3f/{c}.json");                                  // deleted
        _rig.PutRaw($"memories/3f/{SymlinkId}.json", Bytes("x"), "120000");    // rejected
        var second = _rig.Commit("second");
        _rig.CloneGit("fetch", "origin", "main");

        var proven = await Read(second, first, unproven: false);
        var unproven = await Read(second, first, unproven: true);

        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), proven.Changed.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(c, proven.Present);                // the deleted file is not present
        Assert.DoesNotContain(SymlinkId, proven.Changed);        // a rejected id is never "changed"
        Assert.Contains(SymlinkId, proven.Present);
        Assert.Empty(unproven.Changed);                          // no diff when unproven: every accepted record is read instead
    }

    [GitFact]
    public async Task AFailedListing_OrDiff_IsFailed_WithTheKind_AndCancellationIsFlagged()
    {
        var head = EveryShape();

        _rig.Fault = cmd => cmd.IsLsTree ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "fatal: bad object") : null;
        var listing = Assert.IsType<TreeRead.Failed>(await PullTree.ReadAsync(_rig.Client, head, null, true, CancellationToken.None));
        Assert.Contains("list", listing.Reason);
        Assert.False(listing.Cancelled);

        _rig.Fault = cmd => cmd.IsDiffTree ? FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "cancelled") : null;
        var diff = Assert.IsType<TreeRead.Failed>(await PullTree.ReadAsync(_rig.Client, head, head, false, CancellationToken.None));
        Assert.Contains("diff", diff.Reason);
        Assert.True(diff.Cancelled);
    }

    [GitFact]
    public async Task NothingIsReadFromTheWorkingTree()
    {
        var head = EveryShape();
        Directory.Delete(Path.Combine(_rig.Paths.CloneDirectory, "memories"), recursive: true);

        var view = await Read(head);

        Assert.Single(view.Accepted); // the objects are read, whatever the files are
    }
}
