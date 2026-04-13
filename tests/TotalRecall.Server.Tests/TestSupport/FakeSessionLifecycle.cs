// Lightweight ISessionLifecycle fake for Plan 4 Task 4.10 handler tests.
// Records the number of EnsureInitializedAsync invocations, the last
// CancellationToken observed, and returns a caller-supplied
// SessionInitResult. Session id is configurable so session_end tests can
// assert the handler echoes what the lifecycle surfaces.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class FakeSessionLifecycle : ISessionLifecycle
{
    public string SessionIdValue { get; set; } = "sess-fake";
    public SessionInitResult ResultToReturn { get; set; } =
        MakeDefaultResult("sess-fake");

    public int EnsureInitializedCallCount { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public string SessionId => SessionIdValue;
    public bool IsInitialized { get; private set; }

    public Task<SessionInitResult> EnsureInitializedAsync(CancellationToken ct = default)
    {
        EnsureInitializedCallCount++;
        LastToken = ct;
        IsInitialized = true;
        return Task.FromResult(ResultToReturn);
    }

    public static SessionInitResult MakeDefaultResult(string sessionId) => new(
        SessionId: sessionId,
        Project: null,
        ImportSummary: new List<ImportSummaryRow>(),
        WarmSweep: null,
        WarmPromoted: 0,
        ProjectDocs: null,
        HotEntryCount: 0,
        Context: string.Empty,
        TierSummary: new TierSummary(0, 0, 0, 0, 0),
        Hints: new List<string>(),
        LastSessionAge: null,
        SmokeTest: null,
        RegressionAlerts: null,
        Storage: "sqlite");
}
