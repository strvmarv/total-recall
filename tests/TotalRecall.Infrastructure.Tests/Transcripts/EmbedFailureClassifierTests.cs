using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>The worker's three failure classes (search the archive, spec section 3, item 6). Not Integration-trait.</summary>
public sealed class EmbedFailureClassifierTests
{
    // ---------------- CannotRun ----------------

    [Fact]
    public void LocalMissingModel_InvalidOperation_IsCannotRun()
    {
        // ModelManager.EnsureModelAvailable's missing-model error, surfaced through OnnxEmbedder
        var ex = new InvalidOperationException("Model 'bge-small-en-v1.5' is not available at /x/models: run scripts/fetch-bge-small.sh");
        Assert.Equal(EmbedFailureClass.CannotRun, EmbedFailureClassifier.Classify(ex, "local"));
    }

    [Fact]
    public void LocalBadInputs_InvalidOperation_IsCannotRun()
    {
        // OnnxEmbedder's unexpected-inputs error
        var ex = new InvalidOperationException("Unexpected ONNX input 'foo'");
        Assert.Equal(EmbedFailureClass.CannotRun, EmbedFailureClassifier.Classify(ex, "local"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("LOCAL")]
    public void LocalProviderSpellings_InvalidOperation_IsCannotRun(string provider) =>
        Assert.Equal(EmbedFailureClass.CannotRun,
            EmbedFailureClassifier.Classify(new InvalidOperationException("x"), provider));

    [Theory]
    [InlineData("openai")]
    [InlineData("bedrock")]
    public void RemoteMalformedResponse_InvalidOperation_IsChunk(string provider)
    {
        // JsonElement access on a malformed response, or Bedrock's "missing float embeddings"
        var ex = new InvalidOperationException("The requested operation requires an element of type 'Array'");
        Assert.Equal(EmbedFailureClass.Chunk, EmbedFailureClassifier.Classify(ex, provider));
    }

    [Theory]
    [InlineData("local")]
    [InlineData("openai")]
    public void DimensionException_IsCannotRun(string provider) =>
        Assert.Equal(EmbedFailureClass.CannotRun,
            EmbedFailureClassifier.Classify(new EmbedDimensionException(768, 384), provider));

    // ---------------- Systemic ----------------

    [Fact]
    public void IOException_IsSystemic() =>
        Assert.Equal(EmbedFailureClass.Systemic, EmbedFailureClassifier.Classify(new IOException("disk"), "local"));

    [Fact]
    public void TaskCanceled_IsSystemic() =>
        Assert.Equal(EmbedFailureClass.Systemic, EmbedFailureClassifier.Classify(new TaskCanceledException(), "openai"));

    [Fact]
    public void OperationCanceled_IsSystemic() =>
        Assert.Equal(EmbedFailureClass.Systemic, EmbedFailureClassifier.Classify(new OperationCanceledException(), "local"));

    [Fact]
    public void Timeout_IsSystemic() =>
        Assert.Equal(EmbedFailureClass.Systemic, EmbedFailureClassifier.Classify(new TimeoutException(), "openai"));

    [Theory]
    [InlineData(5)]    // SQLITE_BUSY
    [InlineData(6)]    // SQLITE_LOCKED
    [InlineData(517)]  // SQLITE_BUSY_SNAPSHOT (extended code of BUSY)
    public void SqliteBusyOrLocked_IsSystemic(int code) =>
        Assert.Equal(EmbedFailureClass.Systemic,
            EmbedFailureClassifier.Classify(new SqliteException("database is locked", code), "local"));

    [Theory]
    [InlineData(1)]   // SQLITE_ERROR
    [InlineData(19)]  // SQLITE_CONSTRAINT
    public void SqliteOther_IsChunk(int code) =>
        Assert.Equal(EmbedFailureClass.Chunk,
            EmbedFailureClassifier.Classify(new SqliteException("rejected", code), "local"));

    [Fact]
    public void HttpNoStatus_NetworkError_IsSystemic() =>
        Assert.Equal(EmbedFailureClass.Systemic,
            EmbedFailureClassifier.Classify(new HttpRequestException("connection refused"), "openai"));

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(599)]
    public void HttpSystemicStatus_IsSystemic(int status) =>
        Assert.Equal(EmbedFailureClass.Systemic,
            EmbedFailureClassifier.Classify(new HttpRequestException("x", null, (HttpStatusCode)status), "openai"));

    // ---------------- Chunk ----------------

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(422)]
    public void HttpOther4xx_IsChunk(int status) =>
        Assert.Equal(EmbedFailureClass.Chunk,
            EmbedFailureClassifier.Classify(new HttpRequestException("x", null, (HttpStatusCode)status), "openai"));

    [Fact]
    public void KeyNotFound_IsChunk() =>
        Assert.Equal(EmbedFailureClass.Chunk,
            EmbedFailureClassifier.Classify(new KeyNotFoundException("data"), "openai"));

    [Fact]
    public void OnnxRuntimeException_IsChunk()
    {
        // OnnxRuntimeException has no public constructor; an uninitialised instance is enough for a type test.
        var ex = (Exception)RuntimeHelpers.GetUninitializedObject(typeof(OnnxRuntimeException));
        Assert.Equal(EmbedFailureClass.Chunk, EmbedFailureClassifier.Classify(ex, "local"));
    }

    [Fact]
    public void ArgumentException_IsChunk() =>
        Assert.Equal(EmbedFailureClass.Chunk, EmbedFailureClassifier.Classify(new ArgumentException("bad"), "local"));
}
