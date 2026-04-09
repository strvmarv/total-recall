// src/TotalRecall.Infrastructure/Diagnostics/ExceptionLogger.cs
//
// Writes an exception plus its full InnerException chain to stderr in a
// diagnostic-friendly format. Exists because TypeInitializationException
// and DllNotFoundException routinely hide the real failure inside one or
// more nested inner exceptions, and a bare `ex.Message` at the top of a
// catch block surfaces only "A type initializer threw an exception" —
// which is useless when diagnosing a broken AOT publish (missing native
// dep, wrong RID layout, etc).
//
// Callers at boundaries that can plausibly hit static-ctor failures
// (migration guard, server composition, CLI commands that touch the
// embedder or open the DB) should use this instead of inlining their
// own message-only WriteLine. See src/TotalRecall.Host/Program.cs and
// src/TotalRecall.Cli/Commands/StatusCommand.cs for reference call sites.

using System;
using System.IO;

namespace TotalRecall.Infrastructure.Diagnostics;

public static class ExceptionLogger
{
    /// <summary>
    /// Writes <paramref name="prefix"/>, the outer exception type and message,
    /// then each <see cref="Exception.InnerException"/> in the chain, then the
    /// outer stack trace. Safe to call with any exception including ones whose
    /// inner chain is null.
    /// </summary>
    public static void LogChain(TextWriter writer, string prefix, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ex);

        writer.WriteLine($"{prefix}: {ex.GetType().Name}: {ex.Message}");
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            writer.WriteLine($"  -> {inner.GetType().Name}: {inner.Message}");
        }
        if (ex.StackTrace is not null)
        {
            writer.WriteLine(ex.StackTrace);
        }
    }

    /// <summary>
    /// Convenience overload that writes to <see cref="Console.Error"/>.
    /// </summary>
    public static void LogChain(string prefix, Exception ex)
        => LogChain(Console.Error, prefix, ex);
}
