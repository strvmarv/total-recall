using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.AliasTestSupport;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Git sync part 5 (#165), D1 and D3 steps 5 and 7: the <c>git_sync_alias</c> store and the skip plan.</summary>
public sealed class GitSyncAliasesTests : IDisposable
{
    private const string Text = "Use bun, not npm.";
    private static readonly string H = Hash(Text);

    private readonly ScanRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private void Seed(string id, Tier? tier = null, string sourceTool = "claude-code", ContentType? type = null, string? hash = null) =>
        SeedLegacy(_rig.Store, _rig.Connection, id, Text, hash ?? H, tier, sourceTool, type);

    private void Log(string id, string hash, string sourceTool = "claude-code", string path = "/p", ContentType? type = null) =>
        new ImportLog(_rig.Connection).LogImport(sourceTool, path + "/" + id, hash, id, Tier.Warm, type ?? ContentType.Memory);

    private void InsertAlias(string canonical, string other, string? hash, string state) =>
        _rig.Exec(
            $"INSERT INTO git_sync_alias (canonical_id, other_id, origin_hash, state) VALUES ('{canonical}', '{other}', " +
            (hash is null ? "NULL" : $"'{hash}'") + $", '{state}')");

    private List<string> ImportRows()
    {
        var rows = new List<string>();
        using var cmd = _rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, content_hash, target_entry_id, target_type FROM import_log ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add($"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetString(3)}");
        return rows;
    }

    [Fact]
    public void ImportTargets_ReadsEveryToolsMemoryRows_ForExistingEligibleIdsOtherThanTheRecord()
    {
        Seed("cccc1111");
        Seed("aaaa2222", tier: Tier.Cold, sourceTool: "git-sync");
        Log("dddd3333", H); // the entry was deleted: no memory under the id
        Seed("eeee4444", type: ContentType.Knowledge);
        Seed("bbbb5555"); // the record's own id
        Seed("LEGACY_ID"); // an existing entry with an ineligible id
        Seed("ffff6666", hash: Hash("another text"));
        _rig.AddMemory(Tier.Warm, "abcd7777"); // a memory whose only log row of the hash is a knowledge row
        Log("abcd7777", H, type: ContentType.Knowledge);

        var targets = GitSyncAliases.ImportTargets(_rig.Connection, H, "bbbb5555");

        Assert.Equal(["aaaa2222", "cccc1111"], targets);
    }

    [Fact]
    public void ImportTargets_LeavesOutTheRecordsOwnId_EvenWhenItExistsWithAMatchingLogRow()
    {
        Seed("bbbb5555");

        Assert.Empty(GitSyncAliases.ImportTargets(_rig.Connection, H, "bbbb5555"));
        Assert.Equal(["bbbb5555"], GitSyncAliases.ImportTargets(_rig.Connection, H, "aaaa1111"));
    }

    [Fact]
    public void HashesOf_ReadsTheMemoryRowsOfTheId()
    {
        var other = Hash("other");
        Seed("aaaa1111");
        Log("aaaa1111", other, sourceTool: "git-sync");
        Log("aaaa1111", H, sourceTool: "copilot-cli"); // the same hash twice: one value
        Log("aaaa1111", Hash("knowledge"), type: ContentType.Knowledge);
        Log("bbbb2222", Hash("not mine"));

        var hashes = GitSyncAliases.HashesOf(_rig.Connection, "aaaa1111");

        Assert.Equal(new[] { H, other }.OrderBy(x => x, StringComparer.Ordinal), hashes);
    }

    [Fact]
    public void MemoryExists_IsTrueInAnyOfTheThreeTiers_AndFalseForKnowledge()
    {
        _rig.AddMemory(Tier.Hot, "aaaa1111");
        _rig.AddMemory(Tier.Warm, "bbbb2222");
        _rig.AddMemory(Tier.Cold, "cccc3333");
        _rig.AddKnowledge(Tier.Warm, "dddd4444");

        Assert.True(GitSyncAliases.MemoryExists(_rig.Connection, "aaaa1111"));
        Assert.True(GitSyncAliases.MemoryExists(_rig.Connection, "bbbb2222"));
        Assert.True(GitSyncAliases.MemoryExists(_rig.Connection, "cccc3333"));
        Assert.False(GitSyncAliases.MemoryExists(_rig.Connection, "dddd4444"));
        Assert.False(GitSyncAliases.MemoryExists(_rig.Connection, "eeee5555"));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("converged")]
    [InlineData("resolved")]
    [InlineData("abandoned")]
    public void InsertOpen_WritesAnOpenRow_AndLeavesAnExistingRowOfAnyStateAsItIs(string state)
    {
        InsertAlias("aaaa1111", "bbbb2222", "oldhash", state);

        GitSyncAliases.InsertOpen(_rig.Connection, "aaaa1111", "bbbb2222", H);
        GitSyncAliases.InsertOpen(_rig.Connection, "aaaa1111", "cccc3333", H);

        Assert.Equal(
            [$"aaaa1111|bbbb2222|oldhash|{state}", $"aaaa1111|cccc3333|{H}|open"],
            AliasRows(_rig.Connection));
    }

    [Fact]
    public void MarkResolved_InsertsAResolvedRow_OrResolvesAnOpenOne_KeepingItsHash()
    {
        InsertAlias("aaaa1111", "bbbb2222", "oldhash", "open");
        InsertAlias("aaaa1111", "dddd4444", "oldhash", "open");

        GitSyncAliases.MarkResolved(_rig.Connection, "aaaa1111", "bbbb2222", H);
        GitSyncAliases.MarkResolved(_rig.Connection, "aaaa1111", "cccc3333", H);

        Assert.Equal(
            ["aaaa1111|bbbb2222|oldhash|resolved", $"aaaa1111|cccc3333|{H}|resolved", "aaaa1111|dddd4444|oldhash|open"],
            AliasRows(_rig.Connection));
    }

    [Fact]
    public void ReadOpenByHash_AndReadOpenOf_ReadOnlyOpenRows()
    {
        InsertAlias("cccc1111", "xxxx9999", H, "open");
        InsertAlias("aaaa1111", "xxxx9999", H, "open");
        InsertAlias("bbbb1111", "xxxx9999", H, "resolved");
        InsertAlias("dddd1111", "xxxx9999", "otherhash", "open");
        InsertAlias("eeee1111", "xxxx9999", null, "open");
        InsertAlias("aaaa1111", "yyyy9999", H, "open");
        InsertAlias("aaaa1111", "zzzz9999", H, "abandoned");

        var byHash = GitSyncAliases.ReadOpenByHash(_rig.Connection, H)
            .Select(r => $"{r.CanonicalId}|{r.OtherId}|{r.OriginHash}|{r.State}").ToList();
        var ofX = GitSyncAliases.ReadOpenOf(_rig.Connection, "xxxx9999")
            .Select(r => $"{r.CanonicalId}|{r.OtherId}|{r.OriginHash}|{r.State}").ToList();

        Assert.Equal(
            new[] { $"aaaa1111|xxxx9999|{H}|open", $"aaaa1111|yyyy9999|{H}|open", $"cccc1111|xxxx9999|{H}|open" },
            byHash.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            [$"aaaa1111|xxxx9999|{H}|open", $"cccc1111|xxxx9999|{H}|open", "dddd1111|xxxx9999|otherhash|open", "eeee1111|xxxx9999||open"],
            ofX);
    }

    [Fact]
    public void RetargetImports_MovesOnlyTheIdsMemoryRows()
    {
        Log("xxxx1111", H, sourceTool: "claude-code");
        Log("xxxx1111", Hash("b"), sourceTool: "git-sync");
        Log("xxxx1111", Hash("k"), type: ContentType.Knowledge);
        Log("yyyy2222", H);
        var before = ImportRows();

        GitSyncAliases.RetargetImports(_rig.Connection, "xxxx1111", "aaaa0000");

        var expected = before.Select(row =>
        {
            var p = row.Split('|');
            return p[2] == "xxxx1111" && p[3] == "memory" ? $"{p[0]}|{p[1]}|aaaa0000|{p[3]}" : row;
        }).ToList();
        Assert.Equal(expected, ImportRows());
        Assert.Equal(2, ImportRows().Count(r => r.Contains("|aaaa0000|")));
        Assert.Single(ImportRows(), r => r.Contains("|xxxx1111|knowledge"));
    }

    [Fact]
    public void PlanSkip_WithNoTarget_IsNull()
    {
        Seed("bbbb5555"); // only the record's own id
        Log("dddd3333", H); // deleted entry

        Assert.Null(GitSyncAliases.PlanSkip(_rig.Connection, "bbbb5555", H));
        Assert.Null(GitSyncAliases.PlanSkip(_rig.Connection, "eeee6666", Hash("nothing")));
    }

    [Fact]
    public void PlanSkip_GivesTheCanonicalTheTargetsAndTheMissingRows()
    {
        // record higher than the local entry: the local entry is canonical
        Seed("bbbb2222");
        var higher = GitSyncAliases.PlanSkip(_rig.Connection, "cccc3333", H);
        Assert.True(higher!.Matches(new AliasPlan(H, "bbbb2222", ["bbbb2222"], ["cccc3333"])));

        // record lower than the local entry: the record is canonical
        var lower = GitSyncAliases.PlanSkip(_rig.Connection, "aaaa1111", H);
        Assert.True(lower!.Matches(new AliasPlan(H, "aaaa1111", ["bbbb2222"], ["bbbb2222"])));
        Assert.False(lower.IsEmpty);

        // three members: an open row of the hash names a non-local id that joins the group (no row for it)
        InsertAlias("0000aaaa", "bbbb2222", H, "open");
        Seed("dddd4444");
        var three = GitSyncAliases.PlanSkip(_rig.Connection, "cccc3333", H);
        Assert.True(three!.Matches(new AliasPlan(H, "0000aaaa", ["bbbb2222", "dddd4444"], ["cccc3333", "dddd4444"])));

        // an existing resolved row of the pair: nothing to insert
        InsertAlias("0000aaaa", "cccc3333", H, "resolved");
        InsertAlias("0000aaaa", "dddd4444", H, "resolved");
        var none = GitSyncAliases.PlanSkip(_rig.Connection, "cccc3333", H);
        Assert.True(none!.Matches(new AliasPlan(H, "0000aaaa", ["bbbb2222", "dddd4444"], [])));
        Assert.True(none.IsEmpty);
    }

    [Fact]
    public void PlanSkip_AnOpenRowsLocalMember_GetsARow_AndItsNonLocalHalfNone()
    {
        Seed("cccc3333");
        InsertAlias("bbbb2222", "eeee5555", H, "open"); // bbbb2222 is not here, eeee5555 is (with no import_log row)
        _rig.AddMemory(Tier.Warm, "eeee5555");

        var plan = GitSyncAliases.PlanSkip(_rig.Connection, "0000ffff", H);

        Assert.True(plan!.Matches(new AliasPlan(H, "0000ffff", ["cccc3333"], ["cccc3333", "eeee5555"])));
    }

    [Fact]
    public void AliasPlan_Matches_ComparesEveryMember()
    {
        var plan = new AliasPlan("h", "aaaa1111", ["bbbb2222"], ["cccc3333"]);

        Assert.True(plan.Matches(new AliasPlan("h", "aaaa1111", ["bbbb2222"], ["cccc3333"])));
        Assert.False(plan.Matches(null));
        Assert.False(plan.Matches(new AliasPlan("g", "aaaa1111", ["bbbb2222"], ["cccc3333"])));
        Assert.False(plan.Matches(new AliasPlan("h", "aaaa0000", ["bbbb2222"], ["cccc3333"])));
        Assert.False(plan.Matches(new AliasPlan("h", "aaaa1111", ["bbbb2222", "dddd4444"], ["cccc3333"])));
        Assert.False(plan.Matches(new AliasPlan("h", "aaaa1111", ["bbbb2222"], [])));
        Assert.False(plan.IsEmpty);
        Assert.True(new AliasPlan("h", "aaaa1111", ["bbbb2222"], []).IsEmpty);
    }
}
