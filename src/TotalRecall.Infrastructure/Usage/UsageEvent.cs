// src/TotalRecall.Infrastructure/Usage/UsageEvent.cs
//
// Host-neutral usage event record, one per assistant turn. Every host
// adapter translates its transcript format into this shape; the writer
// path (UsageEventLog) persists exactly these fields. Nullability of
// token columns is deliberate — Copilot CLI emits only output_tokens,
// Claude Code emits the full Anthropic usage object. See spec §5.1.

namespace TotalRecall.Infrastructure.Usage;

public sealed record UsageEvent(
    string Host,
    string HostEventId,
    string SessionId,
    long TimestampMs,
    int? TurnIndex,
    string? Model,
    string? ProjectPath,
    string? ProjectRepo,
    string? ProjectBranch,
    string? ProjectCommit,
    string? InteractionId,
    int? InputTokens,
    int? CacheCreation5m,
    int? CacheCreation1h,
    int? CacheRead,
    int? OutputTokens,
    string? ServiceTier,
    string? ServerToolUseJson,
    string? HostRequestId);
