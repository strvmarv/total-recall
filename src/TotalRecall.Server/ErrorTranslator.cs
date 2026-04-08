// src/TotalRecall.Server/ErrorTranslator.cs
//
// Plan 4 Task 4.5 — translate caught exceptions into MCP `ToolCallResult`
// error responses. Three branches:
//
//   1. ModelNotReadyException → structured JSON payload identifying the
//      model, the reason ("missing"/"downloading"/"failed"/"corrupted"),
//      and an optional hint. This is a *known* state, not an internal
//      bug, so we do NOT log to stderr; the host is expected to retry or
//      surface the message to the user.
//
//   2. OperationCanceledException → short "Operation cancelled." text.
//      Cancellation is a normal control-flow signal; no stderr noise.
//
//   3. Anything else → log the full `ex.ToString()` (type + message +
//      stack + inner exceptions) to stderr for operator debugging, but
//      return a *generic* "Internal error: <TypeName>. Check server logs
//      for details." message on the wire. The exception message itself
//      is intentionally NOT echoed back, because it may carry secrets,
//      file paths, connection strings, or other PII.
//
// JSON shape divergence from TS: src-ts/tools/error-translate.ts uses
// `JSON.stringify(obj, null, 2)` (2-space indented). We emit compact
// single-line JSON via the source-generated JsonContext to keep AOT
// reflection-free. MCP hosts parse the payload whitespace-agnostic, so
// the wire contract is preserved.

namespace TotalRecall.Server;

using System;
using System.IO;
using System.Text.Json;
using TotalRecall.Infrastructure.Embedding;

public static class ErrorTranslator
{
    public static ToolCallResult Translate(Exception ex, TextWriter? stderr = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var sink = stderr ?? Console.Error;

        if (ex is ModelNotReadyException mnre)
        {
            var payload = new ModelNotReadyPayload(
                Error: "model_not_ready",
                ModelName: mnre.ModelName,
                Reason: mnre.Reason.ToString().ToLowerInvariant(),
                Hint: mnre.Hint,
                Message: mnre.Message);

            // Source-gen path; no reflection-based Serialize<T>.
            var json = JsonSerializer.Serialize(payload, JsonContext.Default.ModelNotReadyPayload);

            return new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = json } },
                IsError = true,
            };
        }

        if (ex is OperationCanceledException)
        {
            return new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = "Operation cancelled." } },
                IsError = true,
            };
        }

        // Generic branch — log everything we know to stderr, return a
        // sanitized message on the wire.
        sink.WriteLine("total-recall: internal error: " + ex.ToString());

        return new ToolCallResult
        {
            Content = new[]
            {
                new ToolContent
                {
                    Type = "text",
                    Text = $"Internal error: {ex.GetType().Name}. Check server logs for details.",
                },
            },
            IsError = true,
        };
    }
}
