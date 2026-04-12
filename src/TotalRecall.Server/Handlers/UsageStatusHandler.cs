// src/TotalRecall.Server/Handlers/UsageStatusHandler.cs
//
// Task 13 — MCP tool `usage_status`. Wraps UsageQueryService so agents
// can introspect their own token burn mid-session. Input schema mirrors
// the CLI flags of `total-recall usage` (window, group_by, host,
// project, top); JSON output is byte-identical to `total-recall usage
// --json` via the shared UsageJsonRenderer. Closes Phase 2.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Usage;

namespace TotalRecall.Server.Handlers;

public sealed class UsageStatusHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "window": {
              "type": "string",
              "enum": ["5h","1d","7d","30d","90d","all"],
              "description": "Lookback window. Default 7d."
            },
            "group_by": {
              "type": "string",
              "enum": ["host","project","day","model","session","none"],
              "description": "Bucket dimension. Default host."
            },
            "host": {
              "type": "string",
              "description": "Filter to a single host (e.g. claude-code, copilot-cli)."
            },
            "project": {
              "type": "string",
              "description": "Filter to a single project path."
            },
            "top": {
              "type": "integer",
              "minimum": 1,
              "description": "Limit to the top N buckets by total tokens."
            }
          },
          "required": []
        }
        """).RootElement.Clone();

    private readonly UsageQueryService _query;

    public UsageStatusHandler(UsageQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public string Name => "usage_status";

    public string Description =>
        "Get token usage across hosts (claude-code, copilot-cli, ...). Use for "
        + "visibility reports (last 7 days, group by host/project/day/model) and "
        + "for current quota state (last 5h window for claude-code).";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var window = TimeSpan.FromDays(7);
        var groupBy = GroupBy.Host;
        IReadOnlyList<string>? hostFilter = null;
        IReadOnlyList<string>? projectFilter = null;
        var topN = 0;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;

            if (args.TryGetProperty("window", out var wEl) && wEl.ValueKind == JsonValueKind.String)
            {
                var s = wEl.GetString() ?? "";
                if (!TryParseWindow(s, out window))
                    return Task.FromResult(Error(
                        $"usage_status: unknown window '{s}' (expected 5h|1d|7d|30d|90d|all)"));
            }

            if (args.TryGetProperty("group_by", out var gEl) && gEl.ValueKind == JsonValueKind.String)
            {
                var s = gEl.GetString() ?? "";
                if (!TryParseGroupBy(s, out groupBy))
                    return Task.FromResult(Error(
                        $"usage_status: unknown group_by '{s}' (expected host|project|day|model|session|none)"));
            }

            if (args.TryGetProperty("host", out var hEl) && hEl.ValueKind == JsonValueKind.String)
            {
                var h = hEl.GetString();
                if (!string.IsNullOrEmpty(h)) hostFilter = new[] { h };
            }

            if (args.TryGetProperty("project", out var pEl) && pEl.ValueKind == JsonValueKind.String)
            {
                var p = pEl.GetString();
                if (!string.IsNullOrEmpty(p)) projectFilter = new[] { p };
            }

            if (args.TryGetProperty("top", out var tEl) && tEl.ValueKind == JsonValueKind.Number)
            {
                if (tEl.TryGetInt32(out var t) && t >= 1) topN = t;
            }
        }

        // --by session requires --last ≤30d — mirrors UsageCommand.
        if (groupBy == GroupBy.Session && window > TimeSpan.FromDays(30))
            return Task.FromResult(Error(
                "usage_status: group_by=session requires window <=30d (raw event retention window)"));

        var now = DateTimeOffset.UtcNow;
        var query = new UsageQuery(
            Start: now - window,
            End: now,
            HostFilter: hostFilter,
            ProjectFilter: projectFilter,
            GroupBy: groupBy,
            TopN: topN);

        var report = _query.Query(query);
        var jsonText = UsageJsonRenderer.Render(report, query);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static bool TryParseWindow(string s, out TimeSpan window)
    {
        switch (s)
        {
            case "5h":  window = TimeSpan.FromHours(5);    return true;
            case "1d":  window = TimeSpan.FromDays(1);     return true;
            case "7d":  window = TimeSpan.FromDays(7);     return true;
            case "30d": window = TimeSpan.FromDays(30);    return true;
            case "90d": window = TimeSpan.FromDays(90);    return true;
            case "all": window = TimeSpan.FromDays(36500); return true;
            default:    window = default; return false;
        }
    }

    private static bool TryParseGroupBy(string s, out GroupBy g)
    {
        switch (s)
        {
            case "host":    g = GroupBy.Host;    return true;
            case "project": g = GroupBy.Project; return true;
            case "day":     g = GroupBy.Day;     return true;
            case "model":   g = GroupBy.Model;   return true;
            case "session": g = GroupBy.Session; return true;
            case "none":    g = GroupBy.None;    return true;
            default:        g = GroupBy.Host;    return false;
        }
    }

    private static ToolCallResult Error(string message) => new ToolCallResult
    {
        Content = new[] { new ToolContent { Type = "text", Text = message } },
        IsError = true,
    };
}
