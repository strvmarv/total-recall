// tests/TotalRecall.Server.Tests/ErrorTranslatorTests.cs
//
// Plan 4 Task 4.5 — unit tests for ErrorTranslator. Manual fakes only,
// no mocking library. StringWriter is the stderr sink so each test
// asserts both the wire payload and the side-effect log independently.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Text.Json;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Server;
using Xunit;

public sealed class ErrorTranslatorTests
{
    // ---------- helpers ----------

    private static Exception ThrowAndCatch(Func<Exception> factory)
    {
        try
        {
            throw factory();
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    // ---------- ModelNotReadyException ----------

    [Fact]
    public void Translate_ModelNotReady_ReturnsStructuredPayload()
    {
        var ex = new ModelNotReadyException(
            "miniLM",
            ModelNotReadyReason.Missing,
            "downloaded 45/90 MB");

        var stderr = new StringWriter();
        var result = ErrorTranslator.Translate(ex, stderr);

        Assert.True(result.IsError);
        Assert.Single(result.Content);
        Assert.Equal("text", result.Content[0].Type);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        Assert.Equal("model_not_ready", root.GetProperty("error").GetString());
        Assert.Equal("miniLM", root.GetProperty("modelName").GetString());
        Assert.Equal("missing", root.GetProperty("reason").GetString());
        Assert.Equal("downloaded 45/90 MB", root.GetProperty("hint").GetString());
        Assert.Equal(ex.Message, root.GetProperty("message").GetString());
    }

    [Fact]
    public void Translate_ModelNotReady_NullHint_SerializesWithoutHint()
    {
        var ex = new ModelNotReadyException("miniLM", ModelNotReadyReason.Failed, hint: null);

        var stderr = new StringWriter();
        var result = ErrorTranslator.Translate(ex, stderr);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        // JsonContext sets DefaultIgnoreCondition = WhenWritingNull, so the
        // hint field is omitted entirely (matches TS host expectations).
        Assert.False(root.TryGetProperty("hint", out _));
        Assert.Equal("failed", root.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(ModelNotReadyReason.Missing, "missing")]
    [InlineData(ModelNotReadyReason.Downloading, "downloading")]
    [InlineData(ModelNotReadyReason.Failed, "failed")]
    [InlineData(ModelNotReadyReason.Corrupted, "corrupted")]
    public void Translate_ModelNotReady_EachReason(ModelNotReadyReason reason, string expected)
    {
        var ex = new ModelNotReadyException("m", reason);

        var result = ErrorTranslator.Translate(ex, new StringWriter());

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(expected, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Translate_ModelNotReady_DoesNotWriteStderr()
    {
        var ex = new ModelNotReadyException("m", ModelNotReadyReason.Missing, "hint");
        var stderr = new StringWriter();

        ErrorTranslator.Translate(ex, stderr);

        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ---------- generic exception ----------

    [Fact]
    public void Translate_GenericException_WritesStackToStderr_ReturnsSafeMessage()
    {
        // Real throw so we get a real stack trace.
        var ex = ThrowAndCatch(() =>
            new InvalidOperationException("sensitive path: /home/user/.secret"));

        var stderr = new StringWriter();
        var result = ErrorTranslator.Translate(ex, stderr);

        var logged = stderr.ToString();
        Assert.Contains("InvalidOperationException", logged);
        Assert.Contains("sensitive path", logged);
        // Stack frame from this test should appear, proving it's a real stack.
        Assert.Contains("ThrowAndCatch", logged);

        Assert.True(result.IsError);
        Assert.Equal(
            "Internal error: InvalidOperationException. Check server logs for details.",
            result.Content[0].Text);
    }

    [Fact]
    public void Translate_GenericException_PreservesInnerExceptionsInStderr()
    {
        var inner = ThrowAndCatch(() => new ArgumentNullException("paramX"));
        var outer = ThrowAndCatch(() => new InvalidOperationException("outer wrap", inner));

        var stderr = new StringWriter();
        ErrorTranslator.Translate(outer, stderr);

        var logged = stderr.ToString();
        Assert.Contains("InvalidOperationException", logged);
        Assert.Contains("ArgumentNullException", logged);
    }

    // ---------- OperationCanceledException ----------

    [Fact]
    public void Translate_OperationCancelledException_ReturnsCancellationMessage_NoStderr()
    {
        var ex = new OperationCanceledException("canceled by host");
        var stderr = new StringWriter();

        var result = ErrorTranslator.Translate(ex, stderr);

        Assert.True(result.IsError);
        Assert.Equal("Operation cancelled.", result.Content[0].Text);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ---------- argument validation ----------

    [Fact]
    public void Translate_NullException_Throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ErrorTranslator.Translate(null!, new StringWriter()));
    }

    // ---------- PII leakage guard ----------

    [Fact]
    public void Translate_ExceptionMessage_NotLeakedToWire()
    {
        var ex = ThrowAndCatch(() =>
            new InvalidOperationException(
                "database connection string: Server=prod.internal;Password=s3cret"));

        var stderr = new StringWriter();
        var result = ErrorTranslator.Translate(ex, stderr);

        Assert.DoesNotContain("prod.internal", result.Content[0].Text);
        Assert.DoesNotContain("s3cret", result.Content[0].Text);

        // Sanity: the stderr log should still carry the secret-bearing message
        // for the operator. (Server logs are an out-of-band trust boundary.)
        Assert.Contains("prod.internal", stderr.ToString());
    }
}
