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
using System.IO;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Cli.Commands.Transcripts;

public abstract class TranscriptsCommandBase : ICliCommand
{
    public const string SearchNotAvailableLine = "search: not available in this version yet";

    private readonly Func<TranscriptsSettings>? _settings;
    private readonly string? _mainDbPath;
    private readonly Func<string>? _storageMode;
    private readonly TranscriptSentinel? _sentinel;
    private readonly Func<bool>? _isInteractive;
    private readonly Func<string, bool>? _confirm;
    private readonly TextWriter? _output;
    private readonly TextWriter? _error;
    private readonly string? _userConfigPath;

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
        string userConfigPath)
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

    /// <summary>
    /// Opens an existing archive without creating anything. Returns the store, or null with <paramref name="exitCode"/>
    /// set: 0 after saying nothing is stored, 1 after printing why it could not be read.
    /// </summary>
    /// <summary>The wording for an archive that exists but could not be opened (backfill and <see cref="OpenExisting"/>).</summary>
    protected string CouldNotRead(TranscriptLocation location, TranscriptOpenResult result) =>
        $"could not read the archive at {Describe(location)}: {result.Message ?? result.Status.ToString()}";

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
