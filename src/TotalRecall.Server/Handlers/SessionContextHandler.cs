// src/TotalRecall.Server/Handlers/SessionContextHandler.cs
//
// Plan 4 Task 4.10 — ports the `session_context` branch of
// src-ts/tools/session-tools.ts to the .NET Server. Lists the hot-tier
// memory and knowledge entries, formats them as "- {content}[ tags]
// ( project: ...)" lines, and returns `{entryCount, context}`.
//
// Design notes:
//
//   - Format matches TS session-tools.ts:376-380 exactly: tags_suffix is
//     `" [a, b]"` when tags are present, project_suffix is
//     `" (project: foo)"` when a project is set. Lines joined with '\n'.
//     When there are no entries the context string is the sentinel
//     `(no hot tier entries)`.
//
//   - The handler walks only the hot tier. The compactor subagent is the
//     only expected caller and it only ever needs the hot working set.
//
//   - Depends on ISqliteStore directly (no embedder, no search seam).
//     Iterates the F# Entry rows and unwraps FSharpOption<string> for
//     the project field and FSharpList<string> for tags.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>session_context</c> tool. Returns the current
/// hot-tier memory + knowledge rows as a formatted context block for the
/// compactor subagent.
/// </summary>
public sealed class SessionContextHandler : IToolHandler
{
    // Mirror of src-ts/tools/session-tools.ts:120-125 — session_context
    // takes no inputs.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    private const string NoEntriesSentinel = "(no hot tier entries)";

    private readonly ISqliteStore _store;

    public SessionContextHandler(ISqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "session_context";

    public string Description =>
        "Return current hot tier entries as formatted context text";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        _ = arguments; // session_context takes no inputs.

        ct.ThrowIfCancellationRequested();

        var hotMemories = _store.List(Tier.Hot, ContentType.Memory);
        var hotKnowledge = _store.List(Tier.Hot, ContentType.Knowledge);

        var total = hotMemories.Count + hotKnowledge.Count;
        var contextText = total == 0
            ? NoEntriesSentinel
            : BuildContextText(hotMemories, hotKnowledge);

        var dto = new SessionContextResultDto(
            EntryCount: total,
            Context: contextText);

        var jsonText = JsonSerializer.Serialize(
            dto, JsonContext.Default.SessionContextResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static string BuildContextText(
        IReadOnlyList<Entry> hotMemories,
        IReadOnlyList<Entry> hotKnowledge)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var e in hotMemories)
        {
            if (!first) sb.Append('\n');
            AppendLine(sb, e);
            first = false;
        }
        foreach (var e in hotKnowledge)
        {
            if (!first) sb.Append('\n');
            AppendLine(sb, e);
            first = false;
        }

        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, Entry e)
    {
        sb.Append("- ");
        sb.Append(e.Content);

        // Tags suffix: " [a, b]" when non-empty.
        var tags = (IEnumerable<string>)e.Tags;
        var tagList = tags as IList<string> ?? tags.ToList();
        if (tagList.Count > 0)
        {
            sb.Append(" [");
            sb.Append(string.Join(", ", tagList));
            sb.Append(']');
        }

        // Project suffix: " (project: foo)" when set.
        if (FSharpOption<string>.get_IsSome(e.Project))
        {
            sb.Append(" (project: ");
            sb.Append(e.Project.Value);
            sb.Append(')');
        }
    }
}
