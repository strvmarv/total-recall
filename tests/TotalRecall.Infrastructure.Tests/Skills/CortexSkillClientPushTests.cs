using System.Net;
using TotalRecall.Infrastructure.Skills;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

/// <summary>
/// Tests that <see cref="CortexSkillClient.ImportAsync"/> now POSTs to
/// <c>/api/plugin/sync/skills</c> (202, no body) rather than the old
/// <c>/api/me/skills/import</c> endpoint.
/// </summary>
public class CortexSkillClientPushTests
{
    private static (CortexSkillClient client, RecordingHandler handler) Create(RecordingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://cortex.test") };
        return (new CortexSkillClient(http), handler);
    }

    private static ImportedSkill MakeSkill(string name = "demo") =>
        new ImportedSkill(
            Name: name,
            Description: null,
            Content: "# body",
            FrontmatterJson: "{}",
            Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: $"/virt/{name}.md",
            SuggestedScope: "user",
            SuggestedScopeId: "user:u1",
            SuggestedTags: Array.Empty<string>());

    [Fact]
    public async Task ImportAsync_PostsToPluginSyncSkillsEndpoint()
    {
        var handler = new RecordingHandler(content: "", status: HttpStatusCode.Accepted);
        var (client, _) = Create(handler);

        await client.ImportAsync("claude-code", new[] { MakeSkill() }, CancellationToken.None);

        Assert.Equal("POST", handler.LastMethod);
        Assert.NotNull(handler.LastUrl);
        Assert.Contains("/api/plugin/sync/skills", handler.LastUrl);
        Assert.DoesNotContain("/api/me/skills/import", handler.LastUrl);
    }

    [Fact]
    public async Task ImportAsync_DoesNotHitOldImportEndpoint()
    {
        var handler = new RecordingHandler(content: "", status: HttpStatusCode.Accepted);
        var (client, _) = Create(handler);

        await client.ImportAsync("claude-code", new[] { MakeSkill("alpha"), MakeSkill("beta") }, CancellationToken.None);

        Assert.NotNull(handler.LastUrl);
        Assert.DoesNotContain("/api/me/skills/import", handler.LastUrl);
    }

    [Fact]
    public async Task ImportAsync_SerializesAdapterInBody()
    {
        var handler = new RecordingHandler(content: "", status: HttpStatusCode.Accepted);
        var (client, _) = Create(handler);

        await client.ImportAsync("claude-code", new[] { MakeSkill() }, CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"adapter\":\"claude-code\"", handler.LastBody);
    }
}
