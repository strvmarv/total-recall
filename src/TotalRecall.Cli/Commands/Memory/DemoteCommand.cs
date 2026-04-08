// src/TotalRecall.Cli/Commands/Memory/DemoteCommand.cs
//
// Plan 5 Task 5.4 — `total-recall memory demote <id> [--tier warm|cold]
// [--type memory|knowledge]`. Same 4-step sequence as PromoteCommand with
// the inverted direction gate (target must be strictly colder).

using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class DemoteCommand : ICliCommand
{
    private readonly ISqliteStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly IEmbedder? _embedder;

    public DemoteCommand() { }

    // Test/composition seam.
    public DemoteCommand(ISqliteStore store, IVectorSearch vec, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    public string Name => "demote";
    public string? Group => "memory";
    public string Description => "Demote a memory or knowledge entry to a colder tier (also re-embeds)";

    public Task<int> RunAsync(string[] args)
    {
        return Task.FromResult(Execute(args));
    }

    private int Execute(string[] args)
    {
        string? id = null;
        string tierStr = "warm";
        string? typeStr = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--tier":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory demote: --tier requires a value");
                        return 2;
                    }
                    tierStr = args[++i];
                    break;
                case "--type":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory demote: --type requires a value");
                        return 2;
                    }
                    typeStr = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"memory demote: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"memory demote: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("memory demote: <id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        var toTier = TierNames.ParseTier(tierStr);
        if (toTier is null)
        {
            Console.Error.WriteLine($"memory demote: invalid --tier '{tierStr}' (expected warm|cold)");
            return 2;
        }
        // demote targets are strictly colder, so hot is never legal here.
        if (toTier.IsHot)
        {
            Console.Error.WriteLine("memory demote: cannot demote to hot (use promote instead)");
            return 2;
        }

        ContentType? toType = null;
        if (typeStr is not null)
        {
            toType = TierNames.ParseContentType(typeStr);
            if (toType is null)
            {
                Console.Error.WriteLine($"memory demote: invalid --type '{typeStr}' (expected memory|knowledge)");
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
            Console.Error.WriteLine($"memory demote: failed to initialize: {ex.Message}");
            return 1;
        }

        try
        {
            var located = MoveHelpers.Locate(store, id);
            if (located is null)
            {
                Console.Error.WriteLine($"memory demote: entry {id} not found");
                return 1;
            }

            var (fromTier, fromType, entry) = located.Value;
            var targetType = toType ?? fromType;

            // Direction gate: demotion must target a strictly colder tier.
            if (TierNames.WarmthRank(toTier) >= TierNames.WarmthRank(fromTier))
            {
                Console.Error.WriteLine(
                    $"memory demote: cannot demote {TierNames.TierName(fromTier)} -> {TierNames.TierName(toTier)} (target must be colder)");
                return 2;
            }

            MoveHelpers.MoveAndReEmbed(store, vec, embedder, entry, fromTier, fromType, toTier, targetType);

            Console.Out.WriteLine(
                $"demoted {id} from {TierNames.TierName(fromTier)}/{TierNames.ContentTypeName(fromType)} " +
                $"to {TierNames.TierName(toTier)}/{TierNames.ContentTypeName(targetType)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory demote: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory demote <id> [--tier warm|cold] [--type memory|knowledge]");
    }
}
