// src/TotalRecall.Server/ISessionLifecycle.cs
//
// Plan 4 Task 4.3 — DI seam for the session-init pipeline. The MCP wire
// surface (Task 4.10's session_start handler) and the McpServer's
// notifications/initialized fire-and-forget hook both go through this
// interface so unit tests can swap in a hand-rolled fake.

using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server;

/// <summary>
/// Lazy, idempotent session bootstrap. The first call to
/// <see cref="EnsureInitializedAsync"/> runs the host-importer sweep, computes
/// the tier summary, assembles hot-tier context + actionable hints, and caches
/// the resulting <see cref="SessionInitResult"/>. Every subsequent call
/// (regardless of caller — initialized notification, session_start tool,
/// session_context tool) returns the cached value cheaply.
///
/// Mirrors the role of <c>runSessionInit</c> in
/// <c>src-ts/tools/session-tools.ts</c>, scoped down to the Infrastructure
/// pieces that actually exist in .NET as of Plan 4 (importers, SqliteStore,
/// CompactionLog read seam). Everything beyond that surface — warm sweep,
/// semantic warm→hot promotion, project docs auto-ingest, smoke test,
/// regression detection, project detection — is intentionally stubbed and
/// marked with <c>TODO(Plan 5+)</c> at each call site.
/// </summary>
public interface ISessionLifecycle
{
    /// <summary>
    /// Runs session initialization on first invocation, returns the cached
    /// result on every call thereafter. Idempotent; safe to call from
    /// multiple entry points (notification handler, tool handler, etc.).
    /// </summary>
    Task<SessionInitResult> EnsureInitializedAsync(CancellationToken ct = default);

    /// <summary>
    /// True after <see cref="EnsureInitializedAsync"/> has completed at least
    /// once. Lets handlers branch on whether they hit the cached path.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Stable session identifier generated at construction. Exposed so
    /// handlers like <c>session_end</c> can return the session id without
    /// having to round-trip through <see cref="EnsureInitializedAsync"/>.
    /// Task 4.10 backfill.
    /// </summary>
    string SessionId { get; }
}
