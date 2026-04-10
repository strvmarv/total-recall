// src/TotalRecall.Infrastructure/Usage/UsageQuery.cs
//
// Query + report record types for UsageQueryService. Keeps UsageQueryService.cs
// free of type noise. See spec §6.1 for field semantics.

using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Usage;

public enum GroupBy { None, Host, Project, Day, Model, Session }

public sealed record UsageQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string>? HostFilter,      // null = all hosts
    IReadOnlyList<string>? ProjectFilter,   // null = all projects
    GroupBy GroupBy,
    int TopN);                              // 0 = no limit

public sealed record UsageTotals(
    int SessionCount,
    long TurnCount,
    long? InputTokens,                       // null if nothing in bucket had it
    long? CacheCreationTokens,
    long? CacheReadTokens,
    long? OutputTokens);

public sealed record UsageBucket(string Key, UsageTotals Totals);

public sealed record UsageReport(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<UsageBucket> Buckets,
    UsageTotals GrandTotal,
    int SessionsWithFullTokenData,           // at least input_tokens present
    int SessionsWithPartialTokenData);       // output_tokens present but not input
