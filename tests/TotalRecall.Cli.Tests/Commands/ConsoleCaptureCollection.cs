using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

/// <summary>
/// Disables xunit's per-class parallelism for any test that redirects
/// <see cref="System.Console.Out"/> / <see cref="System.Console.Error"/>.
/// Console redirection is process-wide, so two test classes in the same
/// assembly racing on it produce phantom empty captures (Plan 5 Task 5.3a
/// hit this when ReportCommandTests + MigrateCommandTests started
/// interleaving). Apply <c>[Collection("ConsoleCapture")]</c> to every
/// test class that touches the console seam.
/// </summary>
[CollectionDefinition("ConsoleCapture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection { }
