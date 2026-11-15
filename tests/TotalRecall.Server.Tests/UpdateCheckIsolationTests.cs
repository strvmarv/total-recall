// tests/TotalRecall.Server.Tests/UpdateCheckIsolationTests.cs
//
// The background update check must never sit on the session_start path: a request that is still
// unanswered does not delay the tool result. No real network, no real clock (the delay returns at
// once), temp directories only.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.WhatsNew;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class UpdateCheckIsolationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tr-updateiso-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.TrySetResult();
            await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v4.9.0\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task SessionStart_ReturnsWhileUpdateCheckRequestIsPending()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "release-notes.json"),
            "{\"version\":\"4.8.0\",\"previous\":\"4.7.0\",\"summary\":\"Faster search.\"}");
        var service = new WhatsNewService(
            "4.8.0", Path.Combine(_root, "state"), bin, () => true, () => Now, () => null);
        var handler = new PendingHandler();

        using var run = UpdateChecker.Start("4.8.0", Path.Combine(_root, "latest"), true, true, 24, () => null,
            handler, delay: (_, _) => Task.CompletedTask, errors: new StringWriter());
        try
        {
            Assert.NotNull(run);
            await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var sessionStart = new SessionStartHandler(new FakeSessionLifecycle(), whatsNew: service)
                .ExecuteAsync(null, CancellationToken.None);
            var result = await sessionStart.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.IsError ?? false);
            Assert.False(handler.Release.Task.IsCompleted);
        }
        finally
        {
            // Stop the loop first (its delay returns at once and the real clock does not move), then
            // let the pending request go.
            run?.Dispose();
            handler.Release.TrySetResult();
        }
        if (run is not null) await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
