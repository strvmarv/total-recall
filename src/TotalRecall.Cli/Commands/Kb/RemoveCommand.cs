// src/TotalRecall.Cli/Commands/Kb/RemoveCommand.cs
//
// Plan 5 Task 5.7 — `total-recall kb remove <id> [--cascade] [--yes]`. Ports
// src-ts/tools/kb-tools.ts:198-216 (kb_remove). Always deletes the target
// entry; with --cascade, enumerates all cold_knowledge rows and deletes any
// whose ParentId == id OR CollectionId == id first.
//
// Refuses non-interactive removal without --yes to avoid accidental data
// loss from stray scripts. Interactive runs prompt via AnsiConsole.Confirm
// unless the test seam injects a confirmDelegate.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Spectre.Console;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Kb;

public sealed class RemoveCommand : ICliCommand
{
    private readonly ISqliteStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly TextWriter? _out;
    private readonly Func<string, bool>? _confirmDelegate;

    public RemoveCommand() { }

    // Test/composition seam.
    public RemoveCommand(
        ISqliteStore store,
        IVectorSearch vec,
        TextWriter output,
        Func<string, bool>? confirmDelegate = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _out = output ?? throw new ArgumentNullException(nameof(output));
        _confirmDelegate = confirmDelegate;
    }

    public string Name => "remove";
    public string? Group => "kb";
    public string Description => "Remove a knowledge base collection or entry";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        string? id = null;
        bool cascade = false;
        bool yes = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--cascade":
                    cascade = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"kb remove: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"kb remove: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("kb remove: <id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        ISqliteStore store;
        IVectorSearch vec;
        MsSqliteConnection? owned = null;
        try
        {
            if (_store is not null && _vec is not null)
            {
                store = _store;
                vec = _vec;
            }
            else
            {
                var dbPath = ConfigLoader.GetDbPath();
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
                vec = new VectorSearch(owned);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kb remove: failed to open db: {ex.Message}");
            return 1;
        }

        try
        {
            var entry = store.Get(Tier.Cold, ContentType.Knowledge, id);
            if (entry is null)
            {
                Console.Error.WriteLine($"kb remove: entry {id} not found");
                return 1;
            }

            // TTY / --yes gate.
            if (!yes)
            {
                var name = ExtractName(entry.MetadataJson) ?? "(unnamed)";
                if (_confirmDelegate is not null)
                {
                    var prompt = $"Remove {id} ({name}) — this cannot be undone. Continue?";
                    if (!_confirmDelegate(prompt))
                    {
                        WriteOut("aborted");
                        return 0;
                    }
                }
                else if (Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("kb remove: refusing to remove without --yes in non-interactive mode");
                    return 2;
                }
                else
                {
                    var prompt = $"Remove {id} ({name}) — this cannot be undone. Continue?";
                    if (!AnsiConsole.Confirm(prompt, defaultValue: false))
                    {
                        WriteOut("aborted");
                        return 0;
                    }
                }
            }

            int cascadeCount = 0;
            if (cascade)
            {
                var all = store.List(Tier.Cold, ContentType.Knowledge, null);
                var children = new List<Entry>();
                foreach (var e in all)
                {
                    if (IsChildOf(e, id))
                    {
                        children.Add(e);
                    }
                }
                foreach (var child in children)
                {
                    vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, child.Id);
                    store.Delete(Tier.Cold, ContentType.Knowledge, child.Id);
                    cascadeCount++;
                }
            }

            vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, id);
            store.Delete(Tier.Cold, ContentType.Knowledge, id);

            if (cascadeCount > 0)
            {
                WriteOut($"removed {id} + {cascadeCount} children");
            }
            else
            {
                WriteOut($"removed {id}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kb remove: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static bool IsChildOf(Entry e, string id)
    {
        if (FSharpOption<string>.get_IsSome(e.ParentId) && e.ParentId.Value == id) return true;
        if (FSharpOption<string>.get_IsSome(e.CollectionId) && e.CollectionId.Value == id && e.Id != id) return true;
        return false;
    }

    private static string? ExtractName(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("name", out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteOut(string line)
    {
        if (_out is not null) _out.WriteLine(line);
        else Console.Out.WriteLine(line);
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall kb remove <id> [--cascade] [--yes]");
    }
}
