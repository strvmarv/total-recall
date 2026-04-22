using System.Text.Json;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public class PluginSyncSkillDtoTests
{
    [Fact]
    public void Round_trip_through_source_generated_context_preserves_snake_case_fields()
    {
        var dto = new PluginSyncSkillDto(
            Id: Guid.NewGuid(),
            Name: "test",
            Description: "desc",
            Content: "body",
            Scope: "user",
            ScopeId: "u-1",
            Tags: new[] { "tag1", "tag2" },
            Source: "claude-code",
            IsOrphaned: false,
            Version: 2,
            CreatedAt: new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2026, 4, 22, 13, 0, 0, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(
            new[] { dto }, SyncJsonContext.Default.PluginSyncSkillDtoArray);
        Assert.Contains("\"scope_id\":\"u-1\"", json);
        Assert.Contains("\"is_orphaned\":false", json);

        var parsed = JsonSerializer.Deserialize(
            json, SyncJsonContext.Default.PluginSyncSkillDtoArray);
        Assert.NotNull(parsed);
        Assert.Single(parsed!);
        Assert.Equal("test", parsed[0].Name);
        Assert.Equal(2, parsed[0].Version);
    }
}
