// src/TotalRecall.Server/ToolRegistry.cs
//
// Name -> IToolHandler map that is the single source of truth for MCP
// tools/list and tools/call dispatch in TotalRecall.Server. Task 4.2
// introduces this to replace the transitional ToolRegistration/ToolHandler
// delegate surface carried by Task 4.1.
//
// Design notes:
//   - Insertion order is preserved so tools/list output is deterministic and
//     matches the order the Host registered handlers in. We back this with a
//     parallel List<IToolHandler> rather than leaning on Dictionary's
//     documented (but subtly-worded) insertion-order iteration guarantee.
//   - The registry owns no lifecycle beyond the handlers it was handed:
//     handlers that need disposal / warmup are managed by their constructors
//     (e.g., Task 4.6+ handlers take IOnnxEmbedder / IStore instances).
//   - ListTools() returns freshly-built ToolSpec[] rather than caching, on
//     the assumption that registration happens once at startup and tools/list
//     is called at most a handful of times per session.

using System;
using System.Collections.Generic;

namespace TotalRecall.Server;

/// <summary>
/// Registry of <see cref="IToolHandler"/> instances keyed by wire-level tool
/// name. Populated at startup and then read by <see cref="McpServer"/> for
/// <c>tools/list</c> and <c>tools/call</c> dispatch.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolHandler> _byName =
        new(StringComparer.Ordinal);
    private readonly List<IToolHandler> _ordered = new();

    /// <summary>Number of handlers currently registered.</summary>
    public int Count => _ordered.Count;

    /// <summary>
    /// Registers <paramref name="handler"/> under <c>handler.Name</c>.
    /// Throws <see cref="InvalidOperationException"/> if a handler with the
    /// same name is already registered, surfacing collisions at startup rather
    /// than silently shadowing them.
    /// </summary>
    public void Register(IToolHandler handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (string.IsNullOrEmpty(handler.Name))
            throw new ArgumentException("Handler must have a non-empty Name.", nameof(handler));

        if (_byName.ContainsKey(handler.Name))
            throw new InvalidOperationException(
                $"Tool already registered: {handler.Name}");

        _byName.Add(handler.Name, handler);
        _ordered.Add(handler);
    }

    /// <summary>
    /// Returns the <see cref="ToolSpec"/> metadata for every registered
    /// handler, in insertion order. Feeds <c>tools/list</c> verbatim.
    /// </summary>
    public IReadOnlyList<ToolSpec> ListTools()
    {
        var specs = new ToolSpec[_ordered.Count];
        for (var i = 0; i < _ordered.Count; i++)
        {
            var h = _ordered[i];
            specs[i] = new ToolSpec
            {
                Name = h.Name,
                Description = h.Description,
                InputSchema = h.InputSchema,
            };
        }
        return specs;
    }

    /// <summary>
    /// Looks up a handler by wire-level name. Returns <see langword="false"/>
    /// (and sets <paramref name="handler"/> to <see langword="null"/>) when no
    /// such tool is registered.
    /// </summary>
    public bool TryGet(string name, out IToolHandler? handler) =>
        _byName.TryGetValue(name, out handler);
}
