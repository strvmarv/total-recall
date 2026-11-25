// tests/TotalRecall.Server.Tests/HermeticSync.cs
//
// The sync-on-read options that keep a composition-level test off the real Claude Code directory (spec 2026-11-24,
// section 3): no ancestors (so no marker is ever looked for) and no adapters (so the guess enumerates nothing). Every
// Server test that calls a transcript tool through a composition passes these through ArchivePassOptions.Sync, whether
// or not it also redirects HOME and CLAUDE_CONFIG_DIR (ServerTests_CallingTranscriptTools_AreHermetic checks it).

namespace TotalRecall.Server.Tests;

using System;
using TotalRecall.Infrastructure.Transcripts;

internal static class HermeticSync
{
    /// <summary>A sync that finds no current session: nothing is read from the real host directories.</summary>
    public static TranscriptSyncOptions Options { get; } =
        new(Ancestors: () => Array.Empty<int>(), Adapters: () => Array.Empty<ITranscriptAdapter>());
}
