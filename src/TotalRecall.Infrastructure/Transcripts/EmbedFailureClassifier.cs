using System;
using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Data.Sqlite;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The three failure classes of the embed worker (spec section 3, item 6).</summary>
public enum EmbedFailureClass
{
    /// <summary>The embedder cannot run at all: the run ends with the gate's state and no attempt is counted.</summary>
    CannotRun,

    /// <summary>An outage or a busy database: the run ends with <c>embedding failed</c> and no attempt is counted.</summary>
    Systemic,

    /// <summary>Anything else: retried one chunk at a time, with the probe as the health test.</summary>
    Chunk,
}

/// <summary>
/// Sorts an embed failure into its class (spec section 3, item 6). <c>provider</c> is the embedder's provider tag: an
/// <see cref="InvalidOperationException"/> means "cannot run" only from the local ONNX embedder (the missing-model and
/// bad-inputs errors); from a remote embedder it is a malformed response, which goes through the probe like any chunk
/// failure.
/// </summary>
public static class EmbedFailureClassifier
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public static EmbedFailureClass Classify(Exception ex, string provider)
    {
        ArgumentNullException.ThrowIfNull(ex);

        switch (ex)
        {
            case EmbedDimensionException:
                return EmbedFailureClass.CannotRun;
            case IOException:
            case OperationCanceledException: // TaskCanceledException included
            case TimeoutException:
                return EmbedFailureClass.Systemic;
            case SqliteException sql:
                // the primary code is the low byte of an extended code (SQLITE_BUSY_SNAPSHOT is 517)
                return (sql.SqliteErrorCode & 0xFF) is SqliteBusy or SqliteLocked
                    ? EmbedFailureClass.Systemic
                    : EmbedFailureClass.Chunk;
            case HttpRequestException http:
                return IsSystemicStatus(http.StatusCode) ? EmbedFailureClass.Systemic : EmbedFailureClass.Chunk;
            case InvalidOperationException when IsLocal(provider):
                return EmbedFailureClass.CannotRun;
            default:
                return EmbedFailureClass.Chunk;
        }
    }

    /// <summary>No status (a network error), 401, 403, 408, 429 or 5xx.</summary>
    private static bool IsSystemicStatus(HttpStatusCode? status)
    {
        if (status is not { } s)
            return true;
        var code = (int)s;
        return code is 401 or 403 or 408 or 429 || code >= 500;
    }

    private static bool IsLocal(string? provider) =>
        string.IsNullOrWhiteSpace(provider) || string.Equals(provider.Trim(), "local", StringComparison.OrdinalIgnoreCase);
}
