// src/TotalRecall.Server/Handlers/TranscriptSearchDtos.cs
//
// The wire shape of a transcript_search answer (search the archive, spec section 5, "Response"), serialised through
// the source-generated JsonContext (registered there). The context default is to write nulls; the fields that appear
// only when they say something (scopeNote, search.reason, truncated, truncatedHits, note) are omitted when null.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Server;

public sealed record TranscriptSearchModeDto(
    [property: JsonPropertyName("meaning")] string Meaning,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public sealed record TranscriptSearchHitDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("ts")] string? Ts,
    [property: JsonPropertyName("chunkType")] string ChunkType,
    [property: JsonPropertyName("hasError")] bool HasError,
    [property: JsonPropertyName("exchanges")] string Exchanges,
    [property: JsonPropertyName("via")] string[] Via,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("text")] string Text);

public sealed record TranscriptSearchResponseDto(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("scopeNote"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ScopeNote,
    [property: JsonPropertyName("search")] TranscriptSearchModeDto Search,
    [property: JsonPropertyName("pendingEmbedding")] long PendingEmbedding,
    [property: JsonPropertyName("results")] TranscriptSearchHitDto[] Results,
    [property: JsonPropertyName("truncated"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Truncated,
    [property: JsonPropertyName("truncatedHits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TruncatedHits,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note)
{
    /// <summary>The answer of a successful search (an <see cref="TranscriptSearchResponse.Error"/> is not mapped here).</summary>
    public static TranscriptSearchResponseDto From(TranscriptSearchResponse r)
    {
        var hits = new TranscriptSearchHitDto[r.Results.Count];
        for (var i = 0; i < hits.Length; i++)
        {
            var h = r.Results[i];
            var via = new string[h.Via.Count];
            for (var j = 0; j < via.Length; j++) via[j] = h.Via[j];
            hits[i] = new TranscriptSearchHitDto(
                h.Id, h.Session, h.Project, h.Kind, h.Title, h.Ts, h.ChunkType, h.HasError, h.Exchanges, via, h.Score, h.Text);
        }

        var truncated = r.TruncatedHits > 0;
        return new TranscriptSearchResponseDto(
            Enabled: true,
            Query: r.Query,
            Scope: r.Scope,
            ScopeNote: r.ScopeNote,
            Search: new TranscriptSearchModeDto(r.MeaningOn ? "on" : "off", r.MeaningOn ? null : r.MeaningReason),
            PendingEmbedding: r.PendingEmbedding,
            Results: hits,
            Truncated: truncated ? true : null,
            TruncatedHits: truncated ? r.TruncatedHits : null,
            Note: r.Note);
    }
}
