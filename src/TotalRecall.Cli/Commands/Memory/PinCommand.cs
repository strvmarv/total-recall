// src/TotalRecall.Cli/Commands/Memory/PinCommand.cs
//
// Pinned tier Task 10 — `total-recall memory pin <id> [--scope project|global]
// [--project <name>] [--type memory|knowledge]`. CLI twin of the MCP
// memory_pin handler (TotalRecall.Server.Handlers.MemoryPinHandler) with
// identical semantics: moves an entry from any tier into the pinned tier,
// enforces the per-entry content limit at the door, resets decay_score to
// 1.0 on fresh pins, and maps `--scope` onto the PROJECT column
// (global = clear, project = set). Already-pinned entries are an idempotent
// success (scope change still applied when requested).
//
// Ordering matters: the content-limit and project-resolution checks both run
// BEFORE MoveAndReEmbed so a failed pin leaves the entry untouched.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class PinCommand : ICliCommand
{
    // Test seam: all dependencies are injected together so a unit test
    // can record calls against the fakes without hitting real SQLite or ONNX.
    private readonly IStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly IEmbedder? _embedder;
    private readonly int _injectedMaxContentChars = PinnedTierLimits.DefaultMaxContentChars;

    public PinCommand() { }

    // Test/composition seam.
    public PinCommand(IStore store, IVectorSearch vec, IEmbedder embedder,
        int maxContentChars = PinnedTierLimits.DefaultMaxContentChars)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _injectedMaxContentChars = maxContentChars;
    }

    public string Name => "pin";
    public string? Group => "memory";
    public string Description => "Pin an entry: always injected at session start, never decays or compacts";

    public Task<int> RunAsync(string[] args)
    {
        return Task.FromResult(Execute(args));
    }

    private int Execute(string[] args)
    {
        string? id = null;
        string? scopeStr = null;
        string? projectStr = null;
        string? typeStr = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--scope":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory pin: --scope requires a value");
                        return 2;
                    }
                    scopeStr = args[++i];
                    break;
                case "--project":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory pin: --project requires a value");
                        return 2;
                    }
                    projectStr = args[++i];
                    break;
                case "--type":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory pin: --type requires a value");
                        return 2;
                    }
                    typeStr = args[++i];
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"memory pin: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"memory pin: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("memory pin: <id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        if (scopeStr is not null && scopeStr != "project" && scopeStr != "global")
        {
            Console.Error.WriteLine($"memory pin: invalid --scope '{scopeStr}' (expected project|global)");
            return 2;
        }

        ContentType? toType = null;
        if (typeStr is not null)
        {
            toType = TierNames.ParseContentType(typeStr);
            if (toType is null)
            {
                Console.Error.WriteLine($"memory pin: invalid --type '{typeStr}' (expected memory|knowledge)");
                return 2;
            }
        }

        IStore store;
        IVectorSearch vec;
        IEmbedder embedder;
        int maxContentChars;
        MemoryComponents? owned = null;
        try
        {
            if (_store is not null)
            {
                store = _store;
                vec = _vec!;
                embedder = _embedder!;
                maxContentChars = _injectedMaxContentChars;
            }
            else
            {
                owned = MemoryComponents.OpenProduction();
                store = owned.Store;
                vec = owned.Vec;
                embedder = owned.Embedder;
                maxContentChars = ResolvePinnedMaxChars();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory pin: failed to initialize: {ex.Message}");
            return 1;
        }

        try
        {
            var located = MoveHelpers.Locate(store, id);
            if (located is null)
            {
                Console.Error.WriteLine($"memory pin: entry {id} not found");
                return 1;
            }

            var (fromTier, fromType, entry) = located.Value;
            var targetType = toType ?? fromType;

            // Tier model v2 (Task 9): pinning is the `sticky` flag on hot, not a
            // move to the retired pinned tier. The entry must end up in
            // (Hot, targetType); if it is already exactly there, no move is
            // needed. Mirrors MemoryPinHandler.
            var alreadyInHotTarget = fromTier.IsHot && fromType.Equals(targetType);
            var alreadySticky = alreadyInHotTarget && store.IsSticky(targetType, id);

            // Sticky content is injected verbatim every session and never
            // truncated, so size is enforced at the door — before any move.
            if (!alreadySticky && entry.Content.Length > maxContentChars)
            {
                Console.Error.WriteLine(
                    $"memory pin: {PinnedTierLimits.HotContentLimitMessage(maxContentChars, entry.Content.Length)}");
                return 2;
            }

            // Resolve the scope choice BEFORE moving so a project-resolution
            // failure leaves the entry untouched.
            string? effectiveProject = null;
            var clearProject = scopeStr == "global";
            if (scopeStr == "project")
            {
                effectiveProject = projectStr
                    ?? (FSharpOption<string>.get_IsSome(entry.Project) ? entry.Project.Value : null);
                if (string.IsNullOrEmpty(effectiveProject))
                {
                    Console.Error.WriteLine("memory pin: --scope project requires --project (entry has no existing project)");
                    return 2;
                }
            }

            // Move into hot only when not already resident there (enforces the
            // hot cap via the check above; re-embeds under the hot vec table).
            if (!alreadyInHotTarget)
                MoveHelpers.MoveAndReEmbed(store, vec, embedder, entry, fromTier, fromType, Tier.Hot, targetType);

            // Set the sticky flag (skip the write when already sticky).
            if (!alreadySticky)
                store.SetSticky(targetType, id, true);

            // Post-move update: normalize decay_score to 1.0 on fresh pins and
            // apply the scope choice via the project column. Skip the write
            // entirely when the entry was already sticky and no scope change was
            // requested, so we don't spuriously bump updated_at. Mirrors
            // MemoryPinHandler.
            var needsUpdate = !alreadySticky || scopeStr is not null;
            if (needsUpdate)
            {
                store.Update(Tier.Hot, targetType, id, new UpdateEntryOpts
                {
                    DecayScore = alreadySticky ? (double?)null : 1.0,
                    Project = effectiveProject,
                    ClearProject = clearProject,
                });
            }

            Console.Out.WriteLine(
                $"pinned {id} (was {TierNames.TierName(fromTier)}/{TierNames.ContentTypeName(fromType)})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory pin: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Resolves the effective pinned content character limit from
    /// <c>[tiers.pinned] max_content_chars</c> in the effective config,
    /// falling back to <see cref="PinnedTierLimits.DefaultMaxContentChars"/> when
    /// the section is absent. Mirrors ServerComposition.ResolvePinnedMaxChars.
    /// </summary>
    private static int ResolvePinnedMaxChars()
    {
        var cfg = new ConfigLoader().LoadEffectiveConfig();
        return FSharpOption<Core.Config.PinnedTierConfig>.get_IsSome(cfg.Tiers.Pinned)
            ? cfg.Tiers.Pinned.Value.MaxContentChars
            : PinnedTierLimits.DefaultMaxContentChars;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory pin <id> [--scope project|global] [--project <name>] [--type memory|knowledge]");
    }
}
