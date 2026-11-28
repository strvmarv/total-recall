using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Seeding and reading helpers for the alias tests (git sync part 5, #165).</summary>
internal static class AliasTestSupport
{
    /// <summary>A local entry with an <c>import_log</c> row of a host tool, as an import that predates sync leaves it.</summary>
    public static void SeedLegacy(
        SqliteStore store,
        MsSqliteConnection conn,
        string id,
        string text,
        string hash,
        Tier? tier = null,
        string sourceTool = "claude-code",
        ContentType? type = null)
    {
        store.Insert(tier ?? Tier.Warm, type ?? ContentType.Memory, new InsertEntryOpts(Content: text, Id: id));
        new ImportLog(conn).LogImport(
            sourceTool, "/home/u/.claude/memory/" + id + ".md", hash, id, tier ?? Tier.Warm, type ?? ContentType.Memory);
    }

    /// <summary>Every <c>git_sync_alias</c> row as <c>canonical|other|hash|state</c>, ordinal by canonical, then other.</summary>
    public static IReadOnlyList<string> AliasRows(MsSqliteConnection conn) =>
        GitSyncAliases.ReadAll(conn).Select(r => $"{r.CanonicalId}|{r.OtherId}|{r.OriginHash}|{r.State}").ToList();

    public static string Hash(string text) => ImportLog.ContentHash(text);
}
