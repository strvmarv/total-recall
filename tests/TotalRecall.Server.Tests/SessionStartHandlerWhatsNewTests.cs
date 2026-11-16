// tests/TotalRecall.Server.Tests/SessionStartHandlerWhatsNewTests.cs
//
// session_start returns the what's-new notice as a trailing `whatsNew` field,
// built per call outside the cached SessionInitResult. These tests run the
// handler over a WhatsNewService on temp directories only (never the real
// ~/.total-recall) with a hand-written TimeProvider for the 30-second rule.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.WhatsNew;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionStartHandlerWhatsNewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tr-whatsnew-" + Guid.NewGuid().ToString("N"));

    private string State => Path.Combine(_root, "state");
    private string Bin => Path.Combine(_root, "bin");

    public SessionStartHandlerWhatsNewTests()
    {
        Directory.CreateDirectory(Bin);
        File.WriteAllText(Path.Combine(Bin, "release-notes.json"),
            "{\"version\":\"4.8.0\",\"previous\":\"4.7.0\",\"summary\":\"Faster search.\"}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private WhatsNewService MakeService() =>
        new("4.8.0", State, Bin, () => true, () => Now, () => null);

    private WhatsNewService MakeCheckingService() =>
        new("4.8.0", State, Bin, () => true, () => Now, () => null, () => true, () => null);

    private void PutAvailable(string seenVersion)
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "seen-other.json"), "{\"version\":\"" + seenVersion + "\"}");
        File.WriteAllText(Path.Combine(State, "latest-stable.json"),
            "{\"version\":\"4.9.0\",\"summary\":\"Faster sync.\"}");
    }

    private string SeenPath(string host) => Path.Combine(State, "seen-" + host + ".json");

    private static async Task<JsonDocument> Run(
        SessionStartHandler handler, string? argumentsJson = null)
    {
        JsonElement? args = null;
        JsonDocument? argDoc = null;
        if (argumentsJson is not null)
        {
            argDoc = JsonDocument.Parse(argumentsJson);
            args = argDoc.RootElement;
        }
        var result = await handler.ExecuteAsync(args, CancellationToken.None);
        argDoc?.Dispose();
        Assert.False(result.IsError ?? false);
        return JsonDocument.Parse(result.Content[0].Text);
    }

    [Fact]
    public async Task FirstCall_ReturnsFirstSeenNotice_SecondCallReturnsNull()
    {
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeService());

        using var first = await Run(handler);
        var notice = first.RootElement.GetProperty("whatsNew");
        Assert.Equal("first-seen", notice.GetProperty("kind").GetString());
        Assert.StartsWith("total-recall 4.8.0:", notice.GetProperty("text").GetString());
        Assert.Equal(WhatsNewRenderer.Relay, notice.GetProperty("relay").GetString());
        Assert.True(File.Exists(SeenPath("other")));

        using var second = await Run(handler);
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("whatsNew").ValueKind);
    }

    [Fact]
    public async Task HostArgument_Hermes_ReturnsNullAndRecordsNothing()
    {
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeService());

        using var doc = await Run(handler, "{\"host\":\"hermes-mcp-client\"}");

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("whatsNew").ValueKind);
        Assert.False(Directory.Exists(State));
    }

    [Fact]
    public async Task HostArgument_NamesTheHostTheNoticeIsRecordedFor()
    {
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeService());

        using var doc = await Run(handler, "{\"host\":\"claude-code\"}");

        Assert.Equal("first-seen",
            doc.RootElement.GetProperty("whatsNew").GetProperty("kind").GetString());
        Assert.True(File.Exists(SeenPath("claude-code")));
        Assert.False(File.Exists(SeenPath("other")));
    }

    [Fact]
    public async Task FreshDirectStamp_ReturnsNullAndRecordsNothing()
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, "direct-claude-code.json"),
            "{\"at\":\"" + Now.ToString("O") + "\"}");
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeService());

        using var doc = await Run(handler, "{\"host\":\"claude-code\"}");

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("whatsNew").ValueKind);
        Assert.False(File.Exists(SeenPath("claude-code")));
    }

    [Fact]
    public async Task CallOver30Seconds_ReturnsNullAndRecordsNothing_NextCallShowsNotice()
    {
        var clock = new ManualTimeProvider();
        var lifecycle = new AdvancingLifecycle(clock, TimeSpan.FromSeconds(31));
        var handler = new SessionStartHandler(
            lifecycle, whatsNew: MakeService(), timeProvider: clock);

        using var slow = await Run(handler);
        Assert.Equal(JsonValueKind.Null, slow.RootElement.GetProperty("whatsNew").ValueKind);
        Assert.False(File.Exists(SeenPath("other")));

        lifecycle.Delay = TimeSpan.Zero;
        using var fast = await Run(handler);
        Assert.Equal("first-seen",
            fast.RootElement.GetProperty("whatsNew").GetProperty("kind").GetString());
        Assert.True(File.Exists(SeenPath("other")));
    }

    [Fact]
    public async Task CallJustUnder30Seconds_StillReturnsNotice()
    {
        var clock = new ManualTimeProvider();
        var lifecycle = new AdvancingLifecycle(clock, TimeSpan.FromSeconds(29));
        var handler = new SessionStartHandler(
            lifecycle, whatsNew: MakeService(), timeProvider: clock);

        using var doc = await Run(handler);

        Assert.Equal("first-seen",
            doc.RootElement.GetProperty("whatsNew").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task WithoutService_WhatsNewIsNull()
    {
        var handler = new SessionStartHandler(new FakeSessionLifecycle());

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Contains("\"whatsNew\":null", result.Content[0].Text);
    }

    [Fact]
    public async Task ServiceFailure_DoesNotFailSessionStart()
    {
        var service = new WhatsNewService(
            "4.8.0", State, Bin, () => throw new InvalidOperationException("boom"),
            () => Now, () => null);
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: service);

        using var doc = await Run(handler);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("whatsNew").ValueKind);
        Assert.Equal("sess-fake", doc.RootElement.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"claude-code\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"host\":null}")]
    [InlineData("{\"host\":7}")]
    [InlineData("{\"host\":{\"a\":1}}")]
    [InlineData("{\"host\":[\"claude-code\"]}")]
    public async Task NonStringOrMalformedHostArgument_IsIgnored(string argumentsJson)
    {
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeService());

        using var doc = await Run(handler, argumentsJson);

        Assert.Equal("first-seen",
            doc.RootElement.GetProperty("whatsNew").GetProperty("kind").GetString());
        Assert.True(File.Exists(SeenPath("other")));
    }

    [Fact]
    public async Task CachedResultIsNotMutated_PerCallNoticeLeavesLifecycleResultAlone()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionStartHandler(lifecycle, whatsNew: MakeService());

        using var _ = await Run(handler);

        Assert.Null(lifecycle.ResultToReturn.WhatsNew);
    }

    [Fact]
    public async Task AvailableNotice_IsRelayedWithKindAvailable_AndMarked()
    {
        PutAvailable("4.8.0");
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeCheckingService());

        using var doc = await Run(handler);

        var notice = doc.RootElement.GetProperty("whatsNew");
        Assert.Equal("available", notice.GetProperty("kind").GetString());
        Assert.Contains("is available (you have 4.8.0)", notice.GetProperty("text").GetString());
        Assert.Equal("{\"version\":\"4.8.0\",\"availableSeen\":{\"stable\":\"4.9.0\"}}",
            File.ReadAllText(SeenPath("other")));
    }

    [Fact]
    public async Task TwoCallsSameHandler_UpdatedThenNothing_EvenWithAvailableDue()
    {
        PutAvailable("4.7.0");
        var handler = new SessionStartHandler(
            new FakeSessionLifecycle(), whatsNew: MakeCheckingService());

        using var first = await Run(handler);
        Assert.Equal("updated", first.RootElement.GetProperty("whatsNew").GetProperty("kind").GetString());

        using var second = await Run(handler);
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("whatsNew").ValueKind);
    }

    [Fact]
    public async Task CallOver30Seconds_AvailableNotMarked_NextCallShowsIt()
    {
        PutAvailable("4.8.0");
        var before = File.ReadAllText(SeenPath("other"));
        var clock = new ManualTimeProvider();
        var lifecycle = new AdvancingLifecycle(clock, TimeSpan.FromSeconds(31));
        var handler = new SessionStartHandler(
            lifecycle, whatsNew: MakeCheckingService(), timeProvider: clock);

        using var slow = await Run(handler);
        Assert.Equal(JsonValueKind.Null, slow.RootElement.GetProperty("whatsNew").ValueKind);
        Assert.Equal(before, File.ReadAllText(SeenPath("other")));

        lifecycle.Delay = TimeSpan.Zero;
        using var fast = await Run(handler);
        Assert.Equal("available", fast.RootElement.GetProperty("whatsNew").GetProperty("kind").GetString());
        Assert.Contains("availableSeen", File.ReadAllText(SeenPath("other")));
    }

    /// <summary>A TimeProvider whose monotonic timestamp only moves when told to.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalMilliseconds);
    }

    /// <summary>Lifecycle that "takes" a configurable time by advancing the fake clock.</summary>
    private sealed class AdvancingLifecycle : ISessionLifecycle
    {
        private readonly ManualTimeProvider _clock;

        public AdvancingLifecycle(ManualTimeProvider clock, TimeSpan delay)
        {
            _clock = clock;
            Delay = delay;
        }

        public TimeSpan Delay { get; set; }

        public string SessionId => "sess-slow";
        public bool IsInitialized => true;

        public Task<SessionInitResult> EnsureInitializedAsync(CancellationToken ct = default)
        {
            _clock.Advance(Delay);
            return Task.FromResult(FakeSessionLifecycle.MakeDefaultResult("sess-slow"));
        }

        public Task<RefreshResult> RefreshAsync(string? task = null, CancellationToken ct = default)
            => Task.FromResult(new RefreshResult());
    }
}
