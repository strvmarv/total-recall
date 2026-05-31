// src/TotalRecall.Cli/Commands/Memory/RecentCommand.cs
//
// `total-recall memory recent [--limit N] [--tier hot|warm|cold]
//  [--type <entryType>] [--project <p>] [--order created|updated|accessed]
//  [--scope <s> ...] [--json]`
//
// Lists memories newest-first across tiers via Infrastructure RecentQuery.
// Default render: Spectre.Console table. --json: hand-rolled (AOT-safe).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Spectre.Console;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class RecentCommand : ICliCommand
{
    private readonly IStore? _store;

    public RecentCommand() { }

    // Test/composition seam.
    public RecentCommand(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "recent";
    public string? Group => "memory";
    public string Description => "List recent memories newest-first by timestamp";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        int limit = 20;
        Tier? tier = null;
        EntryType? type = null;
        string? project = null;
        string order = "created";
        var scopes = new List<string>();
        bool emitJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json":
                    emitJson = true;
                    break;
                case "--limit":
                    if (i + 1 >= args.Length)
                        return Fail("--limit requires a value");
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
                        return Fail($"invalid --limit '{args[i]}'");
                    if (limit < 1 || limit > 200)
                        return Fail("--limit must be between 1 and 200");
                    break;
                case "--tier":
                    if (i + 1 >= args.Length)
                        return Fail("--tier requires a value");
                    tier = TierNames.ParseTier(args[++i]);
                    if (tier is null)
                        return Fail("--tier must be hot, warm, or cold");
                    break;
                case "--type":
                    if (i + 1 >= args.Length)
                        return Fail("--type requires a value");
                    type = TierNames.ParseEntryType(args[++i]);
                    if (type is null)
                        return Fail("--type must be correction, preference, decision, surfaced, imported, compacted, ingested");
                    break;
                case "--project":
                    if (i + 1 >= args.Length)
                        return Fail("--project requires a value");
                    project = args[++i];
                    break;
                case "--order":
                    if (i + 1 >= args.Length)
                        return Fail("--order requires a value");
                    order = args[++i];
                    if (order is not ("created" or "updated" or "accessed"))
                        return Fail("--order must be created, updated, or accessed");
                    break;
                case "--scope":
                    if (i + 1 >= args.Length)
                        return Fail("--scope requires a value");
                    scopes.Add(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"memory recent: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        IStore store;
        MsSqliteConnection? owned = null;
        try
        {
            if (_store is not null)
            {
                store = _store;
            }
            else
            {
                var dbPath = ConfigLoader.GetDbPath();
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
            }
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain("memory recent: failed to open db", ex);
            return 1;
        }

        try
        {
            var rows = RecentQuery.Run(store, new RecentOptions(
                Limit: limit,
                Tier: tier,
                Type: type,
                Project: project,
                Order: order,
                Scopes: scopes.Count > 0 ? scopes : null));

            if (emitJson)
                Console.Out.WriteLine(SerializeJson(rows, order, tier, type, project));
            else if (rows.Count == 0)
                Console.Out.WriteLine("(no memories yet)");
            else
                Render(rows, order);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory recent: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("memory recent: " + message);
        return 2;
    }

    // ---------- rendering ----------

    private static void Render(IReadOnlyList<(Tier Tier, Entry Entry)> rows, string order)
    {
        var table = new Table().Title("[bold]memory recent[/]");
        table.AddColumn(order == "created" ? "Created" : order == "updated" ? "Updated" : "Accessed");
        table.AddColumn("Tier");
        table.AddColumn("Type");
        table.AddColumn("Project");
        table.AddColumn("Preview");

        foreach (var (t, e) in rows)
        {
            var ts = order == "updated" ? e.UpdatedAt : order == "accessed" ? e.LastAccessedAt : e.CreatedAt;
            table.AddRow(
                Markup.Escape(FormatTimestamp(ts)),
                Markup.Escape(TierNames.TierName(t)),
                Markup.Escape(TierNames.EntryTypeName(e.EntryType)),
                Markup.Escape(OptString(e.Project) ?? "-"),
                Markup.Escape(PreviewText.Collapse(e.Content, PreviewText.DefaultMaxLength)));
        }

        AnsiConsole.Write(table);
    }

    // Listing view: ISO-8601 only (InspectCommand appends the raw-ms suffix; omitted here on purpose).
    private static string FormatTimestamp(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string? OptString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : null;

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(
        IReadOnlyList<(Tier Tier, Entry Entry)> rows,
        string order, Tier? tier, EntryType? type, string? project)
    {
        // JSON shape MUST match MemoryRecentResultDto / RecentEntryDto in
        // TotalRecall.Server.JsonContext. Hand-rolled here (not source-gen) because
        // the AOT-published CLI does not reference TotalRecall.Server.
        var sb = new StringBuilder();
        sb.Append("{\"entries\":[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendEntry(sb, rows[i].Tier, rows[i].Entry);
        }
        sb.Append("],\"count\":"); AppendNumber(sb, rows.Count);
        sb.Append(",\"order\":"); AppendString(sb, order);
        sb.Append(",\"tier\":"); AppendNullableString(sb, tier is { } tf ? TierNames.TierName(tf) : null);
        sb.Append(",\"type\":"); AppendNullableString(sb, type is { } et ? TierNames.EntryTypeName(et) : null);
        sb.Append(",\"project\":"); AppendNullableString(sb, project);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendEntry(StringBuilder sb, Tier tier, Entry e)
    {
        sb.Append('{');
        sb.Append("\"id\":"); AppendString(sb, e.Id);
        sb.Append(",\"tier\":"); AppendString(sb, TierNames.TierName(tier));
        sb.Append(",\"entry_type\":"); AppendString(sb, TierNames.EntryTypeName(e.EntryType));
        sb.Append(",\"project\":"); AppendNullableString(sb, OptString(e.Project));
        sb.Append(",\"created_at\":"); AppendNumber(sb, e.CreatedAt);
        sb.Append(",\"updated_at\":"); AppendNumber(sb, e.UpdatedAt);
        sb.Append(",\"last_accessed_at\":"); AppendNumber(sb, e.LastAccessedAt);
        sb.Append(",\"preview\":"); AppendString(sb, PreviewText.Collapse(e.Content, PreviewText.DefaultMaxLength));
        sb.Append('}');
    }

    private static void AppendNullableString(StringBuilder sb, string? value)
    {
        if (value is null) sb.Append("null");
        else AppendString(sb, value);
    }

    private static void AppendNumber(StringBuilder sb, long v) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendNumber(sb, v);

    private static void AppendString(StringBuilder sb, string s) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory recent [--limit N] [--tier hot|warm|cold] [--type <entryType>] [--project <p>] [--order created|updated|accessed] [--scope <s> ...] [--json]");
    }
}
