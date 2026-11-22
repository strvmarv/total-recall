using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using EmbeddingConfig = TotalRecall.Core.Config.EmbeddingConfig;

namespace TotalRecall.Cli.Commands.Transcripts;

/// <summary>
/// <c>total-recall transcripts status</c> (spec section 8): the setting, whether the archive is active (the sentinel,
/// and a difference between the two), the location, the counts, the forgotten sessions and projects with their
/// identifiers, the cutoff, and (search the archive, spec section 6) after the chunk count the embedding lines: the
/// counts, the embedder and its gate state, whose vectors are stored, the last embed run and how the archive is
/// searched. Needs no terminal and never creates anything: the store is opened only when the archive tables already
/// exist, and the embedder is inspected without creating its models directory. Exit 1 when the archive cannot be located or read (and then it never says that nothing is stored).
/// </summary>
public sealed class StatusCommand : TranscriptsCommandBase
{
    private const int MaxProjectRows = 30;

    public StatusCommand() { }

    public StatusCommand(
        Func<TranscriptsSettings> settings,
        string mainDbPath,
        Func<string> storageMode,
        TranscriptSentinel sentinel,
        Func<bool> isInteractive,
        Func<string, bool> confirm,
        TextWriter output,
        TextWriter error,
        string userConfigPath,
        Func<EmbeddingConfig>? embeddingConfig = null,
        Func<EmbeddingConfig, EmbedderInspectionResult>? inspector = null)
        : base(settings, mainDbPath, storageMode, sentinel, isInteractive, confirm, output, error, userConfigPath,
            embeddingConfig, inspector) { }

    public override string Name => "status";
    public override string Description => "Show whether the transcript archive is on, where it is and what it holds";
    protected override string Usage => "Usage: total-recall transcripts status";

    /// <summary>
    /// Reads the embedder's identity and readiness without a side effect: the local provider's user models directory is
    /// not created, and a failure is reported in the embedder line, not logged.
    /// </summary>
    protected override EmbedderInspectionResult InspectProduction(EmbeddingConfig cfg) =>
        EmbedderInspection.InspectWithoutSideEffects(cfg);

    protected override int Execute(string[] args)
    {
        if (RejectArguments(args) is { } usage)
            return usage;

        return Report();
    }

    private int Report()
    {
        var exit = 0;

        // An unreadable configuration makes the settings loader return defaults: say nothing about the setting or
        // the contents then, only why.
        string storageMode;
        try
        {
            storageMode = ReadStorageMode();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail($"could not read the configuration: {ex.Message}");
        }

        var settings = ReadSettings();
        Out.WriteLine($"enabled = {(settings.Enabled ? "true" : "false")}");

        var sentinel = Sentinel;
        if (RequireAbsolute(sentinel.Path))
        {
            var active = sentinel.IsEnabled();
            Out.WriteLine($"active: {(active ? "yes" : "no")} (sentinel {sentinel.Path} {(active ? "present" : "absent")})");
            if (string.Equals(storageMode, "postgres", StringComparison.OrdinalIgnoreCase))
                Out.WriteLine("storage: postgres, where the transcript archive is not supported");
            if (TranscriptSentinel.IsActive(settings.Enabled, storageMode) != active)
            {
                Out.WriteLine("note: the setting and the sentinel differ; this applies at the next engine start");
            }
        }
        else
        {
            exit = 1;
        }

        var location = ResolveLocation(settings);
        if (location is null)
        {
            Out.WriteLine("location: unavailable");
            return 1;
        }
        Out.WriteLine($"location: {Describe(location)}");

        var opened = TranscriptStore.Open(location, create: false);
        switch (opened.Status)
        {
            case TranscriptOpenStatus.Absent:
                Out.WriteLine($"{(settings.Enabled ? "on" : "off")}, nothing stored");
                return exit;
            case TranscriptOpenStatus.Opened when opened.Store is not null:
                break;
            default:
                opened.Store?.Dispose();
                return Fail($"could not read the archive: {opened.Message ?? opened.Status.ToString()}");
        }

        TranscriptStatus status;
        EmbeddingLines embedding;
        using (var store = opened.Store)
        {
            try
            {
                status = store.Status();
                embedding = ReadEmbedding(store, settings, storageMode);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fail($"could not read the archive: {ex.Message}");
            }
        }

        Print(status, embedding);
        return exit;
    }

    /// <summary>The five embedding lines, in the layout of spec section 6.</summary>
    private sealed record EmbeddingLines(string Counts, string Embedder, string Vectors, string LastRun, string Search);

    private EmbeddingLines ReadEmbedding(ITranscriptStore store, TranscriptsSettings settings, string storageMode)
    {
        var postgres = string.Equals(storageMode, "postgres", StringComparison.OrdinalIgnoreCase);
        var counts = store.CountEmbedding();
        var record = store.ReadEmbedRun();

        using var check = CheckEmbedGate(store, settings, archiveActive: settings.Enabled, postgres: postgres);
        var gate = check.Gate;
        var current = check.Current;

        // A probe-time dimension refusal (the service ignored the configured dimensions) is not visible from config:
        // it holds while the recorded run refused this very provider and model.
        if (gate.CanEmbed && current is not null && record is not null
            && record.StateKind == nameof(EmbedState.DimensionRefused)
            && string.Equals(record.Provider, current.Provider, StringComparison.Ordinal)
            && string.Equals(record.Model, current.Model, StringComparison.Ordinal))
        {
            gate = new EmbedGateResult(EmbedState.DimensionRefused, record.State);
        }

        var countsLine = $"embedding: {Thousands(counts.Embedded)} of {Thousands(counts.Total)} chunks embedded " +
                         $"({Thousands(counts.Waiting)} waiting" +
                         (counts.GaveUp > 0 ? $", {Thousands(counts.GaveUp)} could not be embedded)" : ")");

        string embedder, search;
        if (gate.State == EmbedState.NotApplicable)
        {
            var why = postgres ? "postgres storage" : "the archive is off";
            embedder = $"not applicable ({why})";
            search = $"not available ({why})";
        }
        else
        {
            embedder = gate.CanEmbed && current is not null ? $"{current.Provider} {current.Model} (ready)" : gate.Text;
            search = gate.MeaningUsable ? "meaning and words" : "words only";
        }

        string vectors;
        if (check.Fingerprint is { } state)
        {
            vectors = state switch
            {
                FingerprintState.Match => $"made by {Render(current)}",
                FingerprintState.Mismatch => $"made by {Render(check.Stored)} (not used)",
                _ => "none yet",
            };
        }
        else
        {
            // The running embedder was not inspected (or could not be built): what is stored, from the archive alone.
            var stored = store.ReadEmbedFingerprint();
            vectors = stored is not null && counts.Embedded > 0 ? $"made by {Render(stored)}" : "none yet";
        }

        return new EmbeddingLines(
            countsLine,
            "embedder:  " + embedder,
            "vectors:   " + vectors,
            "last embed run: " + LastRun(record),
            "search:    " + search);
    }

    private static string LastRun(EmbedRunRecord? r)
    {
        if (r is null)
            return "none";
        var at = r.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var how = r.StoppedBy switch
        {
            "done" => "finished",
            "budget" => "stopped by budget",
            "cancelled" => "cancelled",
            "gate" or "failed" or "lease" => "stopped: " + r.State,
            _ => "stopped by " + r.StoppedBy,
        };
        return $"{at}, embedded {N(r.Embedded)}, {N(r.Remaining)} remaining, {how}";
    }

    private static string Render(ArchiveFingerprint? f) => f is null ? "unknown" : $"{f.Provider} {f.Model}";

    private static string Thousands(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    private void Print(TranscriptStatus s, EmbeddingLines embedding)
    {
        s.SessionsByKind.TryGetValue("interactive", out var interactive);
        s.SessionsByKind.TryGetValue("automated", out var automated);

        Out.WriteLine($"schema version: {s.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        Out.WriteLine($"sessions: {N(s.Sessions)} (interactive {N(interactive)}, automated {N(automated)})");
        Out.WriteLine($"chunks: {N(s.Chunks)}");
        Out.WriteLine(embedding.Counts);
        Out.WriteLine(embedding.Embedder);
        Out.WriteLine(embedding.Vectors);
        Out.WriteLine(embedding.LastRun);
        Out.WriteLine(embedding.Search);

        if (s.ByProjectAndKind.Count > 0)
        {
            Out.WriteLine("by project and kind:");
            foreach (var row in s.ByProjectAndKind.Take(MaxProjectRows))
                Out.WriteLine($"  {row.ProjectKey} {row.Kind}: {N(row.Sessions)} sessions, {N(row.Chunks)} chunks");
            if (s.ByProjectAndKind.Count > MaxProjectRows)
                Out.WriteLine($"  and {N(s.ByProjectAndKind.Count - MaxProjectRows)} more");
        }

        if (s.ForgottenSessions.Count == 0)
        {
            Out.WriteLine("forgotten sessions: none");
        }
        else
        {
            Out.WriteLine($"forgotten sessions: {N(s.ForgottenSessions.Count)}");
            foreach (var f in s.ForgottenSessions)
                Out.WriteLine($"  {f.Host}:{f.HostSessionId} (at {f.At})");
        }

        if (s.ForgottenProjects.Count == 0)
        {
            Out.WriteLine("forgotten projects: none");
        }
        else
        {
            Out.WriteLine($"forgotten projects: {N(s.ForgottenProjects.Count)}");
            foreach (var p in s.ForgottenProjects)
            {
                var roots = p.Roots.Count == 0 ? "" : $", directories: {string.Join(", ", p.Roots)}";
                Out.WriteLine($"  {p.ProjectKey} (at {p.At}{roots})");
            }
        }

        Out.WriteLine($"cutoff: {s.IngestNotBefore ?? "none"}");
    }
}
