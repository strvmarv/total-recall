// src/TotalRecall.Cli/Commands/Memory/PromoteCommand.cs
//
// Plan 5 Task 5.4 — `total-recall memory promote <id> [--tier hot|warm]
// [--type memory|knowledge]`. Ports src-ts/memory/promote-demote.ts's
// promoteEntry into a CLI verb. Direction-gated: promotion must target a
// strictly warmer tier than the source. See MoveHelpers.cs for the shared
// locate/move/re-embed plumbing.

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class PromoteCommand : ICliCommand
{
    // Test seam: all three dependencies are injected together so a unit test
    // can record calls against the fakes without hitting real SQLite or ONNX.
    private readonly ISqliteStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly IEmbedder? _embedder;

    public PromoteCommand() { }

    // Test/composition seam.
    public PromoteCommand(ISqliteStore store, IVectorSearch vec, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    public string Name => "promote";
    public string? Group => "memory";
    public string Description => "Promote a memory or knowledge entry to a warmer tier (also re-embeds)";

    public Task<int> RunAsync(string[] args)
    {
        return Task.FromResult(Execute(args));
    }

    private int Execute(string[] args)
    {
        string? id = null;
        string tierStr = "hot";
        string? typeStr = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--tier":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory promote: --tier requires a value");
                        return 2;
                    }
                    tierStr = args[++i];
                    break;
                case "--type":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory promote: --type requires a value");
                        return 2;
                    }
                    typeStr = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"memory promote: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"memory promote: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("memory promote: <id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        var toTierOpt = TierNames.ParseTier(tierStr);
        if (toTierOpt is null)
        {
            Console.Error.WriteLine($"memory promote: invalid --tier '{tierStr}' (expected hot|warm)");
            return 2;
        }
        var toTier = toTierOpt;
        // promote targets are strictly warmer, so cold is never legal here.
        if (toTier.IsCold)
        {
            Console.Error.WriteLine("memory promote: cannot promote to cold (use demote instead)");
            return 2;
        }

        ContentType? toType = null;
        if (typeStr is not null)
        {
            toType = TierNames.ParseContentType(typeStr);
            if (toType is null)
            {
                Console.Error.WriteLine($"memory promote: invalid --type '{typeStr}' (expected memory|knowledge)");
                return 2;
            }
        }

        ISqliteStore store;
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
            Console.Error.WriteLine($"memory promote: failed to initialize: {ex.Message}");
            return 1;
        }

        try
        {
            var located = MoveHelpers.Locate(store, id);
            if (located is null)
            {
                Console.Error.WriteLine($"memory promote: entry {id} not found");
                return 1;
            }

            var (fromTier, fromType, entry) = located.Value;
            var targetType = toType ?? fromType;

            // Direction gate: promotion must target a strictly warmer tier.
            if (TierNames.WarmthRank(toTier) <= TierNames.WarmthRank(fromTier))
            {
                Console.Error.WriteLine(
                    $"memory promote: cannot promote {TierNames.TierName(fromTier)} -> {TierNames.TierName(toTier)} (target must be warmer)");
                return 2;
            }

            MoveHelpers.MoveAndReEmbed(store, vec, embedder, entry, fromTier, fromType, toTier, targetType);

            Console.Out.WriteLine(
                $"promoted {id} from {TierNames.TierName(fromTier)}/{TierNames.ContentTypeName(fromType)} " +
                $"to {TierNames.TierName(toTier)}/{TierNames.ContentTypeName(targetType)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory promote: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory promote <id> [--tier hot|warm] [--type memory|knowledge]");
    }
}
