// src/TotalRecall.Server/Handlers/TranscriptReadDtos.cs
//
// The wire shapes of a transcript_get and a transcript_sessions answer (read spec 2026-11-23, sections 2 and 3),
// serialised through the source-generated JsonContext (registered there). The context default is to write nulls, so
// title, ts, startedAt and endedAt appear as null when unknown; the fields that appear only when they say something
// (truncated, nextFromExchange, nextToExchange, more, automatedHidden, note) are omitted when null.

using System.Text.Json.Serialization;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Server;

public sealed record TranscriptExchangeDto(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("ts")] string? Ts,
    [property: JsonPropertyName("chunkType")] string ChunkType,
    [property: JsonPropertyName("hasError")] bool HasError,
    [property: JsonPropertyName("requested")] bool Requested,
    [property: JsonPropertyName("text")] string Text);

public sealed record TranscriptGetResponseDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("requested")] string Requested,
    [property: JsonPropertyName("context")] int Context,
    [property: JsonPropertyName("exchanges")] TranscriptExchangeDto[] Exchanges,
    [property: JsonPropertyName("approxTokens")] int ApproxTokens,
    [property: JsonPropertyName("truncated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated,
    [property: JsonPropertyName("nextFromExchange"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextFromExchange,
    [property: JsonPropertyName("nextToExchange"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextToExchange,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note)
{
    /// <summary>The answer of a successful get (an <see cref="TranscriptGetResponse.Error"/> is not mapped here).</summary>
    public static TranscriptGetResponseDto From(TranscriptGetResponse r)
    {
        var exchanges = new TranscriptExchangeDto[r.Exchanges.Count];
        for (var i = 0; i < exchanges.Length; i++)
        {
            var e = r.Exchanges[i];
            exchanges[i] = new TranscriptExchangeDto(e.N, e.Ts, e.ChunkType, e.HasError, e.Requested, e.Text);
        }

        return new TranscriptGetResponseDto(
            Enabled: true,
            Session: r.Session,
            Project: r.Project,
            Kind: r.Kind,
            Title: r.Title,
            Requested: r.Requested,
            Context: r.Context,
            Exchanges: exchanges,
            ApproxTokens: r.ApproxTokens,
            Truncated: r.Truncated ? true : null,
            NextFromExchange: r.NextFromExchange,
            NextToExchange: r.NextFromExchange is null ? null : r.NextToExchange,
            Note: r.Note);
    }
}

public sealed record TranscriptSessionDto(
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("startedAt")] string? StartedAt,
    [property: JsonPropertyName("endedAt")] string? EndedAt,
    [property: JsonPropertyName("exchanges")] int Exchanges,
    [property: JsonPropertyName("chunks")] long Chunks);

public sealed record TranscriptSessionsResponseDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("more"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? More,
    [property: JsonPropertyName("automatedHidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AutomatedHidden,
    [property: JsonPropertyName("sessions")] TranscriptSessionDto[] Sessions,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note)
{
    /// <summary>The answer of a successful list (an <see cref="TranscriptSessionsResponse.Error"/> is not mapped here).</summary>
    public static TranscriptSessionsResponseDto From(TranscriptSessionsResponse r)
    {
        var sessions = new TranscriptSessionDto[r.Sessions.Count];
        for (var i = 0; i < sessions.Length; i++)
        {
            var s = r.Sessions[i];
            sessions[i] = new TranscriptSessionDto(s.Session, s.Project, s.Kind, s.Title, s.StartedAt, s.EndedAt, s.Exchanges, s.Chunks);
        }

        return new TranscriptSessionsResponseDto(
            Enabled: true,
            Total: r.Total,
            More: r.More > 0 ? r.More : null,
            AutomatedHidden: r.AutomatedHidden > 0 ? r.AutomatedHidden : null,
            Sessions: sessions,
            Note: r.Note);
    }
}
