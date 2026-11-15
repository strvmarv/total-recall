// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewService.cs
//
// The what's-new notice for one engine process. Check is pure with respect to
// state: it reads the host's seen version and the packaged notes and decides,
// but writes nothing. The caller shows the notice and then calls MarkSeen, so a
// notice that never reached the user is offered again. Construction does no I/O
// (the catalog dump builds the registry). Check never throws.

using System;

namespace TotalRecall.Infrastructure.WhatsNew;

public sealed class WhatsNewService
{
    private readonly string _version;
    private readonly string _stateDir;
    private readonly string _binaryDir;
    private readonly Func<bool> _noticeEnabled;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string?> _envHost;

    public WhatsNewService(
        string version,
        string stateDir,
        string binaryDir,
        Func<bool> noticeEnabled,
        Func<DateTimeOffset> now,
        Func<string?> envHost)
    {
        _version = version;
        _stateDir = stateDir;
        _binaryDir = binaryDir;
        _noticeEnabled = noticeEnabled;
        _now = now;
        _envHost = envHost;
    }

    public PendingNotice? Check(string? clientName)
    {
        try
        {
            var host = HostIds.Resolve(_envHost(), clientName);
            if (host == "hermes") return null;
            if (WhatsNewState.IsDirectStampFresh(_stateDir, host, _now())) return null;

            var seen = WhatsNewState.ReadSeenVersion(_stateDir, host);
            var notes = PackagedReleaseNotes.Read(_binaryDir);
            var notice = WhatsNewDecider.Decide(_version, notes, seen, _noticeEnabled());
            if (notice is null) return null;

            return new PendingNotice(
                WhatsNewRenderer.KindName(notice.Kind),
                WhatsNewRenderer.Line(notice),
                WhatsNewRenderer.Relay,
                _stateDir,
                host,
                notice.Version);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class PendingNotice
{
    private readonly string _stateDir;
    private readonly string _host;
    private readonly string _version;

    public string Kind { get; }
    public string Text { get; }
    public string Relay { get; }

    internal PendingNotice(string kind, string text, string relay, string stateDir, string host, string version)
    {
        Kind = kind;
        Text = text;
        Relay = relay;
        _stateDir = stateDir;
        _host = host;
        _version = version;
    }

    /// <summary>Records the running version (bare, build metadata stripped) as seen by this host. Idempotent; IO errors are swallowed.</summary>
    public void MarkSeen() => WhatsNewState.WriteSeenVersion(_stateDir, _host, _version);
}
