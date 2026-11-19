using System.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>An embedder that counts its calls and can be told to fail.</summary>
internal sealed class CountingEmbedder : IEmbedder
{
    public int Calls { get; private set; }
    public Func<string, bool> Fails { get; set; } = _ => false;
    public EmbedderDescriptor Descriptor { get; } = new("test", "fake", "", 384);

    public float[] Embed(string text)
    {
        Calls++;
        if (Fails(text))
            throw new InvalidOperationException("synthetic embed failure");
        var v = new float[384];
        for (var i = 0; i < 384; i++)
            v[i] = (text.Length % 7 + i % 5 + 1) / 100f;
        return v;
    }
}

/// <summary>
/// A database-only rig for the apply of one entry: a <see cref="ScanRig"/> (joined, with a store and a scanner), a
/// applier whose git client is never called, record builders over the real record writer, and a full dump to compare.
/// </summary>
internal sealed class ApplyRig : IDisposable
{
    public const string OtherMachine = "7b3d1e55";
    public ScanRig Scan { get; } = new();
    public CountingEmbedder Embedder { get; } = new();
    public GitSyncApplier Applier { get; private set; }
    public long Now { get; set; } = 1_800_000_000_000L;
    private readonly GitClient _client;

    public ApplyRig(int hotMaxContentChars = 0)
    {
        var paths = GitSyncPaths.For(Scan.DbPath, "git@github.com:o/r.git", caseInsensitive: false);
        _client = new GitClient(new GitProcess(), "git", paths, new GitClientOptions());
        Options = new ApplyOptions(hotMaxContentChars);
        Applier = NewApplier();
    }

    public ApplyOptions Options { get; }

    public GitSyncApplier NewApplier() => Applier = new GitSyncApplier(_client, Scan.Store, Embedder, Options, () => Now);

    // ---- records ----

    /// <summary>A stamp of the other machine, <paramref name="minute"/> minutes into a fixed hour.</summary>
    public static SyncStamp.Stamp St(int minute) =>
        SyncStamp.StampModule.tryParse($"2026-11-12T09:{minute:00}:00.000Z.000000.{OtherMachine}").Value;

    public static SyncRecordModule.SyncRecord Rec(
        string id,
        string content = "Use bun, not npm.",
        Tier? tier = null,
        bool sticky = false,
        IEnumerable<string>? tags = null,
        string? project = null,
        string? scope = null,
        SourceTool? source = null,
        long createdAt = 1_700_000_000_000L,
        int minute = 10,
        int? tierMinute = null,
        int? stickyMinute = null,
        string? originHash = null) =>
        new(
            id, content, EntryType.Correction,
            ListModule.OfSeq((tags ?? []).Order(StringComparer.Ordinal).Distinct()),
            source is null ? FSharpOption<SourceTool>.None : FSharpOption<SourceTool>.Some(source),
            createdAt,
            project is null ? FSharpOption<string>.None : FSharpOption<string>.Some(project),
            scope is null ? FSharpOption<string>.None : FSharpOption<string>.Some(scope),
            new SyncRecordModule.Placement(sticky ? Tier.Hot : tier ?? Tier.Warm, St(tierMinute ?? minute), sticky, St(stickyMinute ?? minute)),
            St(minute),
            originHash is null ? FSharpOption<SyncRecordModule.Origin>.None : FSharpOption<SyncRecordModule.Origin>.Some(new SyncRecordModule.Origin("claude-code", originHash)),
            FSharpOption<string>.None);

    public static ParsedRecord Parsed(SyncRecordModule.SyncRecord record) =>
        RecordReader.Parse(RecordWriter.Write(record).Bytes!, SyncValidation.recordPath(record.Id)).Parsed
        ?? throw new InvalidOperationException("the record is not valid");

    public EntryResult Apply(SyncRecordModule.SyncRecord? record, bool proven = true, string? blobSha = null) =>
        Apply(record?.Id ?? throw new ArgumentNullException(nameof(record)), record, proven, blobSha);

    /// <summary>
    /// Applies one id with the open held-delete item the pull would pass for it (read from the store, as the pull reads them)
    /// and, when given, the record's blob id.
    /// </summary>
    public EntryResult Apply(string id, SyncRecordModule.SyncRecord? record, bool proven = true, string? blobSha = null) =>
        Applier.ApplyOne(id, record is null ? null : Parsed(record), proven, Held(id), blobSha, guard: default);

    // ---- held-delete items ----

    public HeldItem? Held(string id) => GitSyncReviews.ReadOpenHeld(Scan.Connection).GetValueOrDefault(id);

    public void SeedHeld(string id, string direction = "remote", string cause = "unproven") =>
        GitSyncReviews.UpsertHeld(Scan.Connection, new HeldItem(id, direction, null, cause, 1_700_000_000_000L));

    public int ReviewRows()
    {
        using var cmd = Scan.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM git_sync_review";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- looking ----

    public string Full(bool issues = true)
    {
        var sb = new StringBuilder(Scan.Dump());
        foreach (var (id, row) in Scan.Rows().OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(id).Append(' ').Append(row).Append('\n');
        if (issues)
            foreach (var issue in Scan.Issues())
                sb.Append(issue).Append('\n');
        using (var cmd = Scan.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM git_sync_review ORDER BY entry_id, kind";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                sb.Append("review ");
                for (var i = 0; i < r.FieldCount; i++)
                    sb.Append(r.IsDBNull(i) ? "NULL" : r.GetValue(i)).Append('|');
                sb.Append('\n');
            }
        }
        sb.Append("hlc=").Append(Scan.Meta("git_sync_hlc")).Append('\n');
        return sb.ToString();
    }

    public Found? Entry(string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (Scan.Store.Get(tier, ContentType.Memory, id) is { } e)
                return new Found(tier, e);
        return null;
    }

    public int VectorCount(Tier tier)
    {
        using var cmd = Scan.Connection.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {tier.ToString().ToLowerInvariant()}_memories_vec";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int ImportLogRows(string id)
    {
        using var cmd = Scan.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM import_log WHERE target_entry_id = $id AND source_tool = 'git-sync'";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public ScanSummary RunScan()
    {
        var outcome = Scan.Scanner(Now).Scan();
        return Assert.IsType<ScanOutcome.Done>(outcome).Summary;
    }

    public void Dispose() => Scan.Dispose();
}

internal sealed record Found(Tier Tier, TotalRecall.Core.Entry Row);
