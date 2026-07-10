// src/TotalRecall.Cli/Commands/Memory/UnpinCommand.cs
//
// Pinned tier Task 10 — `total-recall memory unpin <id> [--type memory|knowledge]`.
// CLI twin of the MCP memory_unpin handler
// (TotalRecall.Server.Handlers.MemoryUnpinHandler): moves a pinned entry
// back to warm, resuming the normal decay/compaction lifecycle. Only pinned
// entries may be unpinned — a non-pinned target fails loudly (exit 2)
// without moving anything, intentionally asymmetric with the idempotent pin.

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class UnpinCommand : ICliCommand
{
    private readonly IStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly IEmbedder? _embedder;

    public UnpinCommand() { }

    // Test/composition seam.
    public UnpinCommand(IStore store, IVectorSearch vec, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    public string Name => "unpin";
    public string? Group => "memory";
    public string Description => "Unpin a pinned entry: clears its sticky flag so it stays in hot as an earned resident and the normal decay/compaction lifecycle resumes";

    public Task<int> RunAsync(string[] args)
    {
        return Task.FromResult(Execute(args));
    }

    private int Execute(string[] args)
    {
        string? id = null;
        string? typeStr = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--type":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory unpin: --type requires a value");
                        return 2;
                    }
                    typeStr = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"memory unpin: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"memory unpin: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("memory unpin: <id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        ContentType? toType = null;
        if (typeStr is not null)
        {
            toType = TierNames.ParseContentType(typeStr);
            if (toType is null)
            {
                Console.Error.WriteLine($"memory unpin: invalid --type '{typeStr}' (expected memory|knowledge)");
                return 2;
            }
        }

        IStore store;
        IVectorSearch vec;
        IEmbedder embedder;
        MemoryComponents? owned = null;
        try
        {
            if (_store is not null)
            {
                store = _store;
                vec = _vec!;
                embedder = _embedder!;
            }
            else
            {
                owned = MemoryComponents.OpenProduction();
                store = owned.Store;
                vec = owned.Vec;
                embedder = owned.Embedder;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory unpin: failed to initialize: {ex.Message}");
            return 1;
        }

        try
        {
            var located = MoveHelpers.Locate(store, id);
            if (located is null)
            {
                Console.Error.WriteLine($"memory unpin: entry {id} not found");
                return 1;
            }

            var (fromTier, fromType, entry) = located.Value;

            // Tier model v2 (Task 9): "pinned" is now the sticky flag on hot.
            // Only a sticky-hot entry may be unpinned; anything else is not
            // pinned. Mirrors MemoryUnpinHandler.
            if (!fromTier.IsHot || !store.IsSticky(fromType, id))
            {
                Console.Error.WriteLine(
                    $"memory unpin: entry {id} is not pinned (tier: {TierNames.TierName(fromTier)})");
                return 2;
            }
            var targetType = toType ?? fromType;

            // Clear sticky in place — NO tier move. The entry stays in hot as an
            // earned resident and resumes the normal decay lifecycle.
            store.SetSticky(fromType, id, false);

            Console.Out.WriteLine($"unpinned {id} -> hot/{TierNames.ContentTypeName(targetType)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory unpin: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory unpin <id> [--type memory|knowledge]");
    }
}
