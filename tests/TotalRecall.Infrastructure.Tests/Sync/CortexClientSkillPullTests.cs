using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

/// <summary>
/// Tests for <see cref="CortexClient.GetSkillsModifiedSinceAsync"/>.
/// Verifies that the call is delegated to <see cref="ISkillClient.GetModifiedSinceAsync"/>
/// with the correct <c>since</c> argument.
/// </summary>
public class CortexClientSkillPullTests
{
    // ── fake skill client ─────────────────────────────────────────────────────

    private sealed class FakeSkillClient : ISkillClient
    {
        public DateTime? ReceivedSince { get; private set; }
        public bool WasCalled { get; private set; }

        private readonly PluginSyncSkillDto[] _result;

        public FakeSkillClient(PluginSyncSkillDto[]? result = null)
        {
            _result = result ?? Array.Empty<PluginSyncSkillDto>();
        }

        public Task<PluginSyncSkillDto[]> GetModifiedSinceAsync(DateTime? since, CancellationToken ct)
        {
            WasCalled = true;
            ReceivedSince = since;
            return Task.FromResult(_result);
        }

        // ── unused members ──────────────────────────────────────────────────

        public Task<SkillSearchHitDto[]> SearchAsync(
            string query, string? scope, IReadOnlyList<string>? tags, int limit, CancellationToken ct)
            => Task.FromResult(Array.Empty<SkillSearchHitDto>());

        public Task<SkillBundleDto?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<SkillBundleDto?>(null);

        public Task<SkillBundleDto?> GetByNaturalKeyAsync(
            string name, string scope, string scopeId, CancellationToken ct)
            => Task.FromResult<SkillBundleDto?>(null);

        public Task<SkillListResponseDto> ListAsync(
            string? scope, IReadOnlyList<string>? tags, int skip, int take, CancellationToken ct)
            => Task.FromResult(new SkillListResponseDto(0, 0, 0, Array.Empty<SkillDto>()));

        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.CompletedTask;

        public Task ImportAsync(string adapter, IReadOnlyList<ImportedSkill> skills, CancellationToken ct)
            => Task.CompletedTask;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static CortexClient CreateClient(ISkillClient skillClient)
    {
        // The HttpClient is not exercised by GetSkillsModifiedSinceAsync —
        // the call is forwarded directly to the skill client.
        var http = new HttpClient { BaseAddress = new Uri("http://cortex.test") };
        return new CortexClient(http, skillClient);
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSkillsModifiedSinceAsync_DelegatesToSkillClient()
    {
        var fake = new FakeSkillClient();
        IRemoteBackend remote = CreateClient(fake);

        await remote.GetSkillsModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.True(fake.WasCalled);
    }

    [Fact]
    public async Task GetSkillsModifiedSinceAsync_ForwardsSinceArgument()
    {
        var since = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var fake = new FakeSkillClient();
        IRemoteBackend remote = CreateClient(fake);

        await remote.GetSkillsModifiedSinceAsync(since: since, CancellationToken.None);

        Assert.Equal(since, fake.ReceivedSince);
    }

    [Fact]
    public async Task GetSkillsModifiedSinceAsync_ForwardsNullSince()
    {
        var fake = new FakeSkillClient();
        IRemoteBackend remote = CreateClient(fake);

        await remote.GetSkillsModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.True(fake.WasCalled);
        Assert.Null(fake.ReceivedSince);
    }

    [Fact]
    public async Task GetSkillsModifiedSinceAsync_ReturnsSkillClientResult()
    {
        var dto = new PluginSyncSkillDto(
            Id: Guid.NewGuid(),
            Name: "test-skill",
            Description: null,
            Content: "# body",
            Scope: "global",
            ScopeId: "",
            Tags: Array.Empty<string>(),
            Source: "claude-code",
            IsOrphaned: false,
            Version: 1,
            CreatedAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var fake = new FakeSkillClient(new[] { dto });
        IRemoteBackend remote = CreateClient(fake);

        var result = await remote.GetSkillsModifiedSinceAsync(since: null, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("test-skill", result[0].Name);
    }
}
