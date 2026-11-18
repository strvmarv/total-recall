using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// A push test's world: a <see cref="PullRig"/> (joined machine, database, client) plus a bare remote the clone really
/// pushes to. <see cref="Seed"/> puts a manifest and records on the remote and clones it; <see cref="PullAsync"/> runs
/// the scan, the fetch and the apply as the cycle does; <see cref="Push"/> runs the pusher on their summaries.
/// </summary>
internal sealed class PushRig : IDisposable
{
    public PullRig Pull { get; } = new();
    public BareRemote Remote { get; } = BareRemote.Create(commits: 0);
    public CountingEmbedder Embedder { get; } = new();
    public long Now { get; set; } = 1_800_000_000_000L;
    public const string Label = "testbox";

    public SqliteStore Store => Pull.Store;
    public GitSyncPaths Paths => Pull.Paths;

    /// <summary>A joined machine whose remote holds a manifest and these records, cloned. <c>L</c> is not set until the first <see cref="PullAsync"/>.</summary>
    public string Seed(params SyncRecordModule.SyncRecord[] records) =>
        SeedWith(pull =>
        {
            foreach (var record in records)
                pull.PutRecord(record);
        });

    /// <summary>Like <see cref="Seed"/>, with the remote's files built by the callback (odd shapes, foreign files).</summary>
    public string SeedWith(Action<PullRig> build)
    {
        Pull.Join();
        Pull.PutManifest();
        build(Pull);
        var head = Pull.Commit("seed");
        Pull.Source.PushTo(Remote.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.CloneDirectory)!);
        Pull.Source.Env.RunGit(Pull.Source.Env.Root, "clone", Remote.Url, Paths.CloneDirectory);
        return head;
    }

    // ---- the local store ----

    public string AddMemory(
        Tier tier, string id, string content = "Use bun, not npm.", IReadOnlyList<string>? tags = null, string? project = null,
        string? scope = null, SourceTool? source = null, long createdAt = 1_700_000_000_000L, EntryType? entryType = null) =>
        Store.Insert(tier, ContentType.Memory, new InsertEntryOpts(
            Content: content, SourceTool: source, Project: project, Tags: tags, Id: id, Scope: scope,
            EntryType: entryType, CreatedAt: createdAt));

    /// <summary>Pins a memory: a warm or cold one is promoted to hot first, as the pin tool does.</summary>
    public void Pin(string id)
    {
        foreach (var tier in new[] { Tier.Warm, Tier.Cold })
            if (Store.Get(tier, ContentType.Memory, id) is not null)
                Store.Move(tier, ContentType.Memory, Tier.Hot, ContentType.Memory, id);
        Store.SetSticky(ContentType.Memory, id, true);
    }

    public void Unpin(string id) => Store.SetSticky(ContentType.Memory, id, false);

    public void Edit(Tier tier, string id, UpdateEntryOpts opts) => Store.Update(tier, ContentType.Memory, id, opts);

    // ---- the cycle's first three steps ----

    public ScanSummary Scan() =>
        Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(Pull.Connection, () => Now).Scan()).Summary;

    public GitSyncApplier Applier() => new(Pull.Client, Pull.Store, Embedder, new ApplyOptions(0), () => Now);

    /// <summary>The scan, the fetch and the apply, as the cycle runs them; the apply must finish.</summary>
    public async Task<(ApplySummary Apply, ScanSummary Scan)> PullAsync()
    {
        var scan = Scan();
        var fetch = await Pull.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new InvalidOperationException("the fetch did not succeed: " + fetch),
        };
        var applied = await Applier().ApplyAsync(result, scan);
        return (Assert.IsType<ApplyOutcome.Done>(applied).Summary, scan);
    }

    /// <summary>A completed pull's summary for <paramref name="head"/>, for a test that does not run the apply.</summary>
    public static ApplySummary Summary(string head, bool complete = true) =>
        new(0, 0, 0, 0, 0, complete, head, true, [], [], [], [], [], []);

    public GitSyncPusher Pusher(string name = Label) => new(Pull.Client, Pull.Store, "main", name);

    public Task<PushOutcome> Push(ApplySummary apply, ScanSummary scan, CancellationToken ct = default) =>
        Pusher().PushAsync(apply, scan, ct);

    // ---- looking ----

    public string Remote_Head() => Remote.Head();

    public string RowDump()
    {
        var sb = new StringBuilder();
        foreach (var (id, row) in GitSyncRows.ReadAll(Pull.Connection).OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(id).Append(' ').Append(row).Append('\n');
        sb.Append("last=").Append(Pull.Last).Append('\n');
        sb.Append("ref=").Append(Pull.CloneRef("refs/total-recall/last-synced")).Append('\n');
        return sb.ToString();
    }

    public string Status() => Pull.CloneGit("status", "--porcelain");

    public string? CloneHead() => Pull.CloneRef("HEAD");

    public string RemoteFile(string path, string? rev = null) => Remote.Git("show", (rev ?? "main") + ":" + path);

    public void Dispose()
    {
        Remote.Dispose();
        Pull.Dispose();
    }
}
