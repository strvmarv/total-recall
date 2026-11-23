using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Builds a <see cref="ReviewService"/> over a database file for the held-deletion review tests (milestone 1b-iii part 2):
/// a <see cref="SyncSetupEnvironment"/> like <see cref="SetupRig.Env"/> (a stub public probe, a fake config file next to the
/// database, a fixed host identity) whose clock is <paramref name="nowMs"/>.
/// </summary>
internal static class ReviewServices
{
    /// <param name="embedder">The embedder the service creates on first use; none: a <see cref="CountingEmbedder"/>.</param>
    /// <param name="syncDirectory">Create the database's sync directory first, as it is once a cycle has run; <c>false</c> serves the tests that the service creates it.</param>
    public static ReviewService For(
        string dbPath,
        Func<long> nowMs,
        IEmbedder? embedder = null,
        int hotMaxContentChars = 0,
        int pinnedMaxContentChars = 500,
        string? loadError = null,
        bool syncDirectory = true)
    {
        if (syncDirectory)
            GitSyncPaths.ForDatabase(dbPath, caseInsensitive: false).EnsureCreated();
        var setup = new SyncSetupEnvironment(
            dbPath,
            CaseInsensitive: false,
            GuardSettings.For(null),
            "git",
            PathVariable: null,
            new GitProcess(),
            GitOptions: null,
            new StubProbe(() => 404),
            new FakeUserConfig(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "config.toml")),
            new HostIdentity(() => new MachineIdResult(MachineIdKind.Found, "machine-one"), dbPath, false),
            () => DateTimeOffset.FromUnixTimeMilliseconds(nowMs()),
            n => new byte[n],
            "9.9.9",
            _ => { },
            Fault: null,
            Move: null,
            loadError);
        return new ReviewService(new ReviewEnvironment(setup, () => embedder ?? new CountingEmbedder(), hotMaxContentChars, pinnedMaxContentChars));
    }

    /// <summary>The database's sync directory (the one <see cref="GitSyncPaths.ForDatabase"/> names).</summary>
    public static string SyncDirectory(string dbPath) => GitSyncPaths.ForDatabase(dbPath, caseInsensitive: false).SyncDirectory;
}

/// <summary>Held-delete items made the way the pull makes them, over an <see cref="ApplyRig"/>.</summary>
internal static class ReviewHolds
{
    /// <summary>The memory pulled (a row active with a base), then its record gone in an unproven pull: an open remote hold.</summary>
    public static void RemoteHold(this ApplyRig rig, string id, string content = "Kept here.")
    {
        rig.Apply(ApplyRig.Rec(id, content));
        rig.Apply(id, null, proven: false);
        Assert.Equal("remote", GitSyncReviews.ReadHeldItem(rig.Scan.Connection, id)?.Direction);
    }

    /// <summary>
    /// The memory pulled, deleted here and scanned, then its record (blob id <paramref name="blobSha"/>) seen by an unproven
    /// pull: an open local hold whose hash is the blob id; with <paramref name="approve"/> the item is then approved.
    /// </summary>
    public static void LocalHold(this ApplyRig rig, string id, string blobSha, string content = "Deleted here.", bool approve = false)
    {
        var record = ApplyRig.Rec(id, content);
        rig.Apply(record);
        rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, id);
        rig.RunScan();
        rig.Apply(record, proven: false, blobSha: blobSha);
        Assert.Equal(blobSha, GitSyncReviews.ReadHeldItem(rig.Scan.Connection, id)?.RecordHash);
        if (approve)
            GitSyncReviews.ApproveHeld(rig.Scan.Connection, id);
    }
}
