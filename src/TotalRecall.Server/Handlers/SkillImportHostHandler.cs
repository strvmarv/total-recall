// src/TotalRecall.Server/Handlers/SkillImportHostHandler.cs
//
// Plan 2 Task 13 — MCP handler for the `skill_import_host` tool. Scans
// local Claude Code skills (global + project scopes) via ISkillImportService
// and pushes them to cortex. The supplied delegate produces the current
// project path at call-time so the handler stays testable; production
// composition wires it to the working directory / IDE-provided project
// context.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;

namespace TotalRecall.Server.Handlers;

public sealed class SkillImportHostHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse(
        """{"type":"object","properties":{}}""").RootElement.Clone();

    private readonly ISkillImportService _importService;
    private readonly Func<string?> _projectPathProvider;

    public SkillImportHostHandler(
        ISkillImportService importService,
        Func<string?> projectPathProvider)
    {
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _projectPathProvider = projectPathProvider ?? throw new ArgumentNullException(nameof(projectPathProvider));
    }

    public string Name => "skill_import_host";
    public string Description => "Scan local Claude Code skills and push them to cortex";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? _, CancellationToken ct)
    {
        var summaries = await _importService
            .ImportAsync(_projectPathProvider(), ct)
            .ConfigureAwait(false);

        var text = JsonSerializer.Serialize(
            summaries, JsonContext.Default.SkillImportSummaryDtoArray);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }
}
