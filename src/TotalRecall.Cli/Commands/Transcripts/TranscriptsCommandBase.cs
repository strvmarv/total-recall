// Shared by the five `transcripts` commands (spec section 8): the injected seams, the terminal gate, the confirmation,
// the selector parsing of forget/unforget and the location of the archive.
//
// Exit codes: 0 done (also a declined confirmation, which changes nothing), 1 failure, 2 usage error or refusal to run
// without a terminal. A terminal means standard input is not redirected; there is no override flag (scripts and the
// documented hand edit use config.toml).
//
// The parameterless constructor stores nothing: the registry is rebuilt on every invocation, so every production input
// (config, data directory, database path) is resolved when the command runs.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Spectre.Console;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Transcripts;
using EmbeddingConfig = TotalRecall.Core.Config.EmbeddingConfig;

namespace TotalRecall.Cli.Commands.Transcripts;

public abstract class TranscriptsCommandBase : ICliCommand
{
    private readonly Func<TranscriptsSettings>? _settings;
    private readonly string? _mainDbPath;
    private readonly Func<string>? _storageMode;
    private readonly TranscriptSentinel? _sentinel;
    private readonly Func<bool>? _isInteractive;
    private readonly Func<string, bool>? _confirm;
    private readonly TextWriter? _output;
    private readonly TextWriter? _error;
    private readonly string? _userConfigPath;
    private readonly Func<EmbeddingConfig>? _embeddingConfig;
    private readonly Func<EmbeddingConfig, EmbedderInspectionResult>? _inspector;

    /// <summary>Production: every input is resolved when the command runs.</summary>
    protected TranscriptsCommandBase() { }

    /// <summary>Test seam.</summary>
    protected TranscriptsCommandBase(
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
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mainDbPath = mainDbPath ?? throw new ArgumentNullException(nameof(mainDbPath));
        _storageMode = storageMode ?? throw new ArgumentNullException(nameof(storageMode));
        _sentinel = sentinel ?? throw new ArgumentNullException(nameof(sentinel));
        _isInteractive = isInteractive ?? throw new ArgumentNullException(nameof(isInteractive));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _userConfigPath = userConfigPath ?? throw new ArgumentNullException(nameof(userConfigPath));
        _embeddingConfig = embeddingConfig;
        _inspector = inspector;
    }

    public abstract string Name { get; }
    public string? Group => "transcripts";
    public abstract string Description { get; }
    public bool Hidden => false;

    protected abstract string Usage { get; }

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args ?? Array.Empty<string>()));

    protected abstract int Execute(string[] args);

    // ---- seams ---------------------------------------------------------------------------------------------------

    protected TextWriter Out => _output ?? Console.Out;
    protected TextWriter Err => _error ?? Console.Error;

    protected TranscriptsSettings ReadSettings() => _settings is not null ? _settings() : ConfigLoader.LoadTranscriptsSettings();

    protected string ReadStorageMode() => _storageMode is not null
        ? _storageMode()
        : TranscriptSentinel.EffectiveStorageMode(new ConfigLoader().LoadEffectiveConfig());

    protected TranscriptSentinel Sentinel => _sentinel ?? new TranscriptSentinel(ConfigLoader.GetDataDir());

    protected string UserConfigPath => _userConfigPath ?? Path.Combine(ConfigLoader.GetDataDir(), "config.toml");

    /// <summary>The main database path; throws <see cref="SqliteDbPathException"/> for an invalid TOTAL_RECALL_DB_PATH.</summary>
    protected string MainDbPath() => _mainDbPath ?? ConfigLoader.GetDbPath();

    /// <summary>The effective <c>[embedding]</c> configuration.</summary>
    protected EmbeddingConfig ReadEmbeddingConfig() => _embeddingConfig is not null
        ? _embeddingConfig()
        : new ConfigLoader().LoadEffectiveConfig().Embedding;

    /// <summary>
    /// Builds the configured embedder and reads its identity and readiness. The caller disposes the embedder. Call it
    /// only after rules 1 and 2 of the gate passed from config: a failed inspection logs a stack trace.
    /// </summary>
    protected EmbedderInspectionResult Inspect()
    {
        var cfg = ReadEmbeddingConfig();
        try
        {
            return _inspector is not null ? _inspector(cfg) : EmbedderInspection.Inspect(cfg);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain($"[total-recall] transcripts {Name}: building the embedder", ex);
            return new EmbedderInspectionResult(null, null, new EmbedderReadiness(false, ex.Message), ex.Message);
        }
    }

    private bool IsInteractive() => _isInteractive is not null ? _isInteractive() : !Console.IsInputRedirected;

    /// <summary>Asks; false (the default answer) means no.</summary>
    protected bool Confirm(string prompt) => _confirm is not null
        ? _confirm(prompt)
        : AnsiConsole.Confirm(Markup.Escape(prompt), defaultValue: false);

    // ---- shared steps --------------------------------------------------------------------------------------------

    protected string Prefix => $"transcripts {Name}";

    protected int UsageError(string message)
    {
        Err.WriteLine($"{Prefix}: {message}");
        Err.WriteLine(Usage);
        return 2;
    }

    protected int Fail(string message)
    {
        Err.WriteLine($"{Prefix}: {message}");
        return 1;
    }

    /// <summary>For the commands that take no arguments: a usage error (exit 2) or null.</summary>
    protected int? RejectArguments(string[] args) =>
        args.Length > 0 ? UsageError("unexpected argument") : null;

    /// <summary>The terminal gate: exit 2 with a message when standard input is redirected, else null.</summary>
    protected int? RequireTerminal()
    {
        if (IsInteractive())
            return null;
        Err.WriteLine($"{Prefix} must be run in a terminal: it asks for confirmation, and there is no flag to skip it.");
        return 2;
    }

    /// <summary>True when <paramref name="path"/> is absolute; else prints why the data directory is unusable.</summary>
    protected bool RequireAbsolute(string path)
    {
        if (Path.IsPathRooted(path))
            return true;
        Err.WriteLine($"{Prefix}: the data directory cannot be resolved ('{path}' is not an absolute path); " +
                      "set HOME or TOTAL_RECALL_HOME");
        return false;
    }

    /// <summary>
    /// The archive's location from the settings and the main database path, or null after printing why it cannot be
    /// resolved (an invalid <c>transcripts.db_path</c>, an invalid TOTAL_RECALL_DB_PATH, an unresolvable data directory).
    /// </summary>
    protected TranscriptLocation? ResolveLocation(TranscriptsSettings settings)
    {
        // A malformed config.toml makes the settings loader fall back to the defaults, which would point these
        // commands at the main database while a separate archive file exists: read the configuration strictly first.
        if (!ConfigReadable())
            return null;

        string mainDbPath;
        try
        {
            mainDbPath = MainDbPath();
        }
        catch (Exception ex) when (ex is SqliteDbPathException or ArgumentException)
        {
            Err.WriteLine($"{Prefix}: {ex.Message}");
            return null;
        }
        if (!RequireAbsolute(mainDbPath))
            return null;

        var location = TranscriptStore.ResolveLocation(settings, mainDbPath, out var error);
        if (location is null)
            Err.WriteLine($"{Prefix}: the archive is unavailable: {error}");
        return location;
    }

    /// <summary>
    /// True when the configuration parses (the strict read throws on a malformed file); else prints
    /// "could not read the configuration" and returns false.
    /// </summary>
    protected bool ConfigReadable()
    {
        try
        {
            ReadStorageMode();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail($"could not read the configuration: {ex.Message}");
            return false;
        }
    }

    /// <summary>How a location reads in output and prompts (the form <c>Status().Location</c> uses).</summary>
    protected static string Describe(TranscriptLocation location) =>
        location.IsMainDatabase ? $"main database ({location.Path})" : location.Path;

    /// <summary>"the main database (&lt;path&gt;)" or "the file &lt;path&gt;", for running text.</summary>
    protected static string The(TranscriptLocation location) =>
        location.IsMainDatabase ? "the " + Describe(location) : "the file " + location.Path;

    protected static string Plural(long n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    // ---- the embed gate and the embed step (embed, backfill, status) ---------------------------------------------

    /// <summary>
    /// The gate's verdict for this command (<c>ServerPass = false</c>) with what it read. Disposing it disposes the
    /// embedder the inspection built.
    /// </summary>
    protected sealed class EmbedCheck : IDisposable
    {
        public EmbedCheck(EmbedGateResult gate, EmbedderInspectionResult? inspected, FingerprintState? fingerprint,
            ArchiveFingerprint? stored)
        {
            Gate = gate;
            Inspected = inspected;
            Fingerprint = fingerprint;
            Stored = stored;
        }

        public EmbedGateResult Gate { get; }
        public EmbedderInspectionResult? Inspected { get; }

        /// <summary>The fingerprint state against the running embedder; null when it was not read.</summary>
        public FingerprintState? Fingerprint { get; }

        /// <summary>The stored fingerprint when <see cref="Fingerprint"/> is a mismatch.</summary>
        public ArchiveFingerprint? Stored { get; }

        public ArchiveFingerprint? Current => Inspected?.Current;

        public void Dispose()
        {
            try { (Inspected?.Embedder as IDisposable)?.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExceptionLogger.LogChain("[total-recall] transcripts: disposing the embedder", ex);
            }
        }
    }

    /// <summary>
    /// Evaluates the embed gate (spec section 3). Rules 1 and 2 are evaluated from config first, so an archive that is
    /// off, postgres, or a remote provider without <c>allow_remote_embedding</c> never builds (or inspects) an embedder
    /// and no archive text can reach it. Store reads may throw; the caller handles that.
    /// </summary>
    protected EmbedCheck CheckEmbedGate(ITranscriptStore store, TranscriptsSettings settings, bool archiveActive, bool postgres)
    {
        var cfg = ReadEmbeddingConfig();
        var provider = FSharpOption<string>.get_IsSome(cfg.Provider) ? cfg.Provider.Value : "local";
        var allowRemote = settings.AllowRemoteEmbedding;

        var preliminary = EmbedGate.Evaluate(new EmbedGateInput(
            ArchiveActive: archiveActive, Postgres: postgres, Provider: provider, AllowRemoteEmbedding: allowRemote,
            Dimensions: null, Fingerprint: null, Stored: null, Current: null,
            Readiness: new EmbedderReadiness(true, null), MemoryReindexRunning: false, ServerPass: false));
        if (!preliminary.CanEmbed)
            return new EmbedCheck(preliminary, null, null, null);

        var inspected = Inspect();
        try
        {
            var current = inspected.Current;
            FingerprintState? state = null;
            ArchiveFingerprint? stored = null;
            if (current is not null)
            {
                state = store.CheckFingerprint(current);
                if (state == FingerprintState.Mismatch)
                    stored = store.ReadEmbedFingerprint();
            }

            var gate = EmbedGate.Evaluate(new EmbedGateInput(
                ArchiveActive: archiveActive,
                Postgres: postgres,
                Provider: provider,
                AllowRemoteEmbedding: allowRemote,
                Dimensions: current?.Dimensions,
                Fingerprint: state,
                Stored: stored,
                Current: current,
                Readiness: inspected.Readiness,
                MemoryReindexRunning: false,
                ServerPass: false));
            if (gate.CanEmbed && (inspected.Embedder is null || current is null))
                gate = EmbedGate.NotReady(inspected.FactoryError ?? "no embedder");
            return new EmbedCheck(gate, inspected, state, stored);
        }
        catch
        {
            try { (inspected.Embedder as IDisposable)?.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ExceptionLogger.LogChain($"[total-recall] transcripts {Name}: disposing the embedder", ex);
            }
            throw;
        }
    }

    /// <summary>How long the progress line waits before it is printed again.</summary>
    protected static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the worker with no budget through a ready <paramref name="check"/>, printing
    /// <c>embedded N of M (K remaining)</c> after the first batch and then at most once per <see cref="ProgressInterval"/>.
    /// </summary>
    protected EmbedRun RunWorker(ITranscriptStore store, EmbedCheck check, long total, TimeProvider time,
        CancellationToken ct, string indent)
    {
        DateTimeOffset? last = null;
        var worker = new TranscriptEmbedWorker(store, check.Inspected!.Embedder!, time);
        return worker.Run(check.Gate, check.Current!, new EmbedBudget(null, null), ct, p =>
        {
            var now = time.GetUtcNow();
            if (last is { } at && now - at < ProgressInterval)
                return;
            last = now;
            Out.WriteLine($"{indent}embedded {N(p.Embedded)} of {N(total)} ({N(p.Remaining)} remaining)");
        });
    }

    /// <summary>
    /// The chunks this run could not embed, exactly: those that failed alone in it (the ones that reached their third try
    /// included; the others still wait and the next run tries them again).
    /// </summary>
    protected static int CouldNotEmbed(EmbedRun run) => run.FailedAlone;

    /// <summary>Waits for Ctrl+C (the first press cancels between steps, a second ends the process) unless injected.</summary>
    protected static T WithCancelKey<T>(CancellationToken? injected, Func<CancellationToken, T> run)
    {
        if (injected is { } token)
            return run(token);

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => e.Cancel = KeepProcessOnCancelKey(cts);
        Console.CancelKeyPress += handler;
        try
        {
            return run(cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// The Ctrl+C handler's decision, the value for <see cref="ConsoleCancelEventArgs.Cancel"/>. The first press cancels
    /// <paramref name="cts"/> and keeps the process, so the run stops at its next check (backfill after the current
    /// session, embed between batches) and prints what it did; a second press (the run is already stopping) returns false
    /// and ends the process as usual.
    /// </summary>
    public static bool KeepProcessOnCancelKey(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        var keep = !cts.IsCancellationRequested;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return keep;
    }

    protected static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    // ---- forget / unforget selectors -----------------------------------------------------------------------------

    protected enum SelectorKind { Session, Project, Everything }

    protected sealed record Selector(SelectorKind Kind, string Host, string Id, string Key)
    {
        public string SessionText => $"{Host}:{Id}";
    }

    /// <summary>
    /// Exactly one of <c>--session &lt;host&gt;:&lt;id&gt;</c>, <c>--project &lt;key&gt;</c> or
    /// <paramref name="everythingFlag"/>. A session selector splits at the first colon; an empty host, id or key is a
    /// usage error. Null after a usage error was printed.
    /// </summary>
    protected Selector? ParseSelector(string[] args, string everythingFlag)
    {
        Selector? selector = null;
        var count = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--session" || a == "--project")
            {
                if (i + 1 >= args.Length)
                {
                    UsageError($"{a} needs a value");
                    return null;
                }
                var value = args[++i];
                count++;
                if (a == "--session")
                {
                    var colon = value.IndexOf(':');
                    if (colon <= 0 || colon == value.Length - 1 || string.IsNullOrWhiteSpace(value[..colon])
                        || string.IsNullOrWhiteSpace(value[(colon + 1)..]))
                    {
                        UsageError("--session takes <host>:<session id>, both non-empty");
                        return null;
                    }
                    selector = new Selector(SelectorKind.Session, value[..colon], value[(colon + 1)..], string.Empty);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        UsageError("--project needs a non-empty key");
                        return null;
                    }
                    selector = new Selector(SelectorKind.Project, string.Empty, string.Empty, value);
                }
            }
            else if (a == everythingFlag)
            {
                count++;
                selector = new Selector(SelectorKind.Everything, string.Empty, string.Empty, string.Empty);
            }
            else if (a.StartsWith('-'))
            {
                UsageError($"unknown option '{a}'");
                return null;
            }
            else
            {
                UsageError("unexpected argument");
                return null;
            }
        }

        if (count != 1)
        {
            UsageError("exactly one selector is required");
            return null;
        }
        return selector;
    }

    /// <summary>The wording for an archive that exists but could not be opened (backfill and <see cref="OpenExisting"/>).</summary>
    protected string CouldNotRead(TranscriptLocation location, TranscriptOpenResult result) =>
        $"could not read the archive at {Describe(location)}: {result.Message ?? result.Status.ToString()}";

    /// <summary>
    /// Opens an existing archive without creating anything. Returns the store, or null with <paramref name="exitCode"/>
    /// set: 0 after saying nothing is stored, 1 after printing why it could not be read.
    /// </summary>
    protected ITranscriptStore? OpenExisting(TranscriptLocation location, string nothingToDo, out int exitCode)
    {
        var result = TranscriptStore.Open(location, create: false);
        switch (result.Status)
        {
            case TranscriptOpenStatus.Opened when result.Store is not null:
                exitCode = 0;
                return result.Store;
            case TranscriptOpenStatus.Absent:
                Out.WriteLine($"nothing is stored in the transcript archive ({Describe(location)}); {nothingToDo}");
                exitCode = 0;
                return null;
            default:
                exitCode = Fail(CouldNotRead(location, result));
                return null;
        }
    }
}
