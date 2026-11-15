// src/TotalRecall.Infrastructure/WhatsNew/WhatsNewService.cs
//
// The what's-new notice for one engine process. Check is pure with respect to
// state: it reads the host's seen version and the packaged notes and decides,
// but writes nothing. The caller shows the notice and then calls MarkSeen, so a
// notice that never reached the user is offered again. Construction does no I/O
// (the catalog dump builds the registry). Check never throws.

using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.WhatsNew;

public sealed class WhatsNewService
{
    private readonly string _version;
    private readonly string _stateDir;
    private readonly string _binaryDir;
    private readonly Func<bool> _noticeEnabled;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string?> _envHost;
    private readonly Func<bool>? _checkSetting;
    private readonly Func<string?>? _noUpdateCheckEnv;

    // Hosts this engine process has already told. Filled only by PendingNotice.MarkSeen, so a
    // notice that never reached the user is offered again.
    private readonly HashSet<string> _marked = new(StringComparer.Ordinal);
    private readonly object _markedLock = new();

    public WhatsNewService(
        string version,
        string stateDir,
        string binaryDir,
        Func<bool> noticeEnabled,
        Func<DateTimeOffset> now,
        Func<string?> envHost,
        Func<bool>? checkSetting = null,
        Func<string?>? noUpdateCheckEnv = null)
    {
        _version = version;
        _stateDir = stateDir;
        _binaryDir = binaryDir;
        _noticeEnabled = noticeEnabled;
        _now = now;
        _envHost = envHost;
        _checkSetting = checkSetting;
        _noUpdateCheckEnv = noUpdateCheckEnv;
    }

    internal string StateDir => _stateDir;

    internal void RememberMarked(string host)
    {
        lock (_markedLock) _marked.Add(host);
    }

    private bool IsMarked(string host)
    {
        lock (_markedLock) return _marked.Contains(host);
    }

    public PendingNotice? Check(string? clientName)
    {
        try
        {
            var host = HostIds.Resolve(_envHost(), clientName);
            if (host == "hermes") return null;
            if (IsMarked(host)) return null;
            if (WhatsNewState.IsDirectStampFresh(_stateDir, host, _now())) return null;

            var seen = WhatsNewState.ReadSeen(_stateDir, host);
            var notes = PackagedReleaseNotes.Read(_binaryDir);
            var noticeOn = _noticeEnabled();

            var channel = UpdateChannel.Of(_version);
            LatestRelease? latest = null;
            if (UpdateSettings.CheckOn(noticeOn, _checkSetting?.Invoke() ?? false, _noUpdateCheckEnv?.Invoke()))
            {
                var file = LatestReleaseState.Read(_stateDir, channel);
                latest = file?.Version is { } v ? new LatestRelease(v, file.Summary ?? "") : null;
            }
            seen.AvailableSeen.TryGetValue(channel, out var availableSeen);

            var notice = WhatsNewDecider.Decide(_version, notes, seen.Version, noticeOn, latest, availableSeen);
            if (notice is null) return null;

            return new PendingNotice(
                WhatsNewRenderer.KindName(notice.Kind),
                WhatsNewRenderer.Line(notice, host),
                WhatsNewRenderer.Relay,
                this,
                host,
                notice);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class PendingNotice
{
    private readonly WhatsNewService _owner;
    private readonly string _host;
    private readonly Notice _notice;

    public string Kind { get; }
    public string Text { get; }
    public string Relay { get; }

    internal PendingNotice(string kind, string text, string relay, WhatsNewService owner, string host, Notice notice)
    {
        Kind = kind;
        Text = text;
        Relay = relay;
        _owner = owner;
        _host = host;
        _notice = notice;
    }

    /// <summary>
    /// Records that this host was told: the engine will not offer another notice to the host, and the
    /// state file gets the seen version (first-seen, updated) or the available mark for the running
    /// channel (available). Idempotent; IO errors are swallowed.
    /// </summary>
    public void MarkSeen()
    {
        _owner.RememberMarked(_host);
        if (_notice.Kind == NoticeKind.Available && _notice.Channel is not null)
            WhatsNewState.WriteAvailableSeen(_owner.StateDir, _host, _notice.Channel, _notice.Version);
        else
            WhatsNewState.WriteSeenVersion(_owner.StateDir, _host, _notice.Version);
    }
}
