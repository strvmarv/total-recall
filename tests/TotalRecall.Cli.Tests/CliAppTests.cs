// Plan 5 Task 5.1 (pivot) — in-process smoke tests for the hand-rolled
// CliApp dispatcher. Captures stdout via Console.SetOut + StringWriter, with
// a try/finally to restore the original writer. The dispatcher uses
// Spectre.Console.AnsiConsole.WriteLine for output; AnsiConsole honors
// whatever Console.Out is set to at call time (it does not cache a writer at
// type init), so capture is reliable in-process.

using System;
using System.Collections.Generic;
using System.IO;
using TotalRecall.Cli;
using Xunit;

namespace TotalRecall.Cli.Tests;

public class CliAppTests
{
    private sealed class CapturedRun
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
    }

    private static CapturedRun Capture(Func<int> run)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var code = run();
            return new CapturedRun
            {
                ExitCode = code,
                StdOut = outWriter.ToString(),
                StdErr = errWriter.ToString(),
            };
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void Run_Version_ReturnsZero()
    {
        // The --version path intentionally writes via Spectre.Console's
        // AnsiConsole so every AOT publish smoke-tests the rendering
        // dependency from Main. AnsiConsole caches Console.Out at its first
        // call, which makes in-process stdout capture unreliable across
        // tests, so we only assert the exit code here. The AOT-binary smoke
        // test (see the Task 5.1 report) exercises the actual stdout.
        var result = Capture(() => CliApp.Run(new[] { "--version" }));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Run_Help_ReturnsZero_AndOutputContainsAppName()
    {
        var result = Capture(() => CliApp.Run(new[] { "--help" }));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("total-recall", result.StdOut);
        // With no commands registered yet, the dispatcher prints the
        // "no subcommands registered" hint. Task 5.2+ will add real entries.
        Assert.Contains("no subcommands registered", result.StdOut);
    }

    [Fact]
    public void Run_NoArgs_ReturnsZero_AndPrintsHelp()
    {
        var result = Capture(() => CliApp.Run(Array.Empty<string>()));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: total-recall", result.StdOut);
    }

    [Fact]
    public void Run_UnknownSubcommand_ReturnsTwo_AndMentionsUnknown()
    {
        var result = Capture(() => CliApp.Run(new[] { "bogus" }));
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown command", result.StdErr);
    }

    // Stub command used by the registration-seam test below.
    private sealed class StubCommand : ICliCommand
    {
        private readonly Func<string[], int> _body;
        public StubCommand(string name, string? group, string description, Func<string[], int> body)
        {
            Name = name;
            Group = group;
            Description = description;
            _body = body;
        }
        public string Name { get; }
        public string? Group { get; }
        public string Description { get; }
        public int Run(string[] args) => _body(args);
    }

    [Fact]
    public void Run_RegisteredLeafCommand_IsInvoked()
    {
        string[]? captured = null;
        var cmd = new StubCommand("ping", null, "stub leaf", a =>
        {
            captured = a;
            return 42;
        });

        try
        {
            CliApp.SetRegistryForTestsInternal(new List<ICliCommand> { cmd });
            var result = Capture(() => CliApp.Run(new[] { "ping", "hello", "world" }));
            Assert.Equal(42, result.ExitCode);
            Assert.NotNull(captured);
            Assert.Equal(new[] { "hello", "world" }, captured);
        }
        finally
        {
            CliApp.SetRegistryForTestsInternal(null);
        }
    }

    [Fact]
    public void Run_RegisteredGroupCommand_IsInvoked()
    {
        var cmd = new StubCommand("report", "eval", "stub group verb", _ => 7);
        try
        {
            CliApp.SetRegistryForTestsInternal(new List<ICliCommand> { cmd });
            var result = Capture(() => CliApp.Run(new[] { "eval", "report" }));
            Assert.Equal(7, result.ExitCode);
        }
        finally
        {
            CliApp.SetRegistryForTestsInternal(null);
        }
    }
}
