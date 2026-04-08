// src/TotalRecall.Infrastructure/Search/IHybridSearch.cs
//
// Minimal interface extracted from HybridSearch so handlers in
// TotalRecall.Server (e.g. MemorySearchHandler — Plan 4 Task 4.7) can take a
// seam rather than the concrete orchestration class. This keeps the server
// tests free of real SQLite/vector/FTS wiring: they can hand the handler a
// recording fake that returns a configurable SearchResult[] without having
// to subclass HybridSearch (which is sealed and owns three dependencies).
//
// The interface is intentionally tiny — just the single Search entry point
// the handler calls. HybridSearch remains the composition root for the
// vector/FTS/store trio and retains its existing ctor and implementation.

using System.Collections.Generic;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// Seam for the hybrid (vector + FTS) search orchestrator. Implemented by
/// <see cref="HybridSearch"/> in production and by test doubles in handler
/// unit tests.
/// </summary>
public interface IHybridSearch
{
    /// <summary>
    /// Run hybrid search across the given <paramref name="tiers"/>. See
    /// <see cref="HybridSearch.Search"/> for the semantics.
    /// </summary>
    IReadOnlyList<SearchResult> Search(
        IReadOnlyList<(Tier Tier, ContentType Type)> tiers,
        string query,
        System.ReadOnlyMemory<float> queryEmbedding,
        HybridSearchOpts opts);
}
