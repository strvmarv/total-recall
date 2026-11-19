namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Where the transcript archive lives: the main database (<paramref name="IsMainDatabase"/>) or a separate
/// <c>transcripts.db_path</c> file. <paramref name="Path"/> is a full path. Built by
/// <see cref="TranscriptStore.ResolveLocation"/>.
/// </summary>
public sealed record TranscriptLocation(string Path, bool IsMainDatabase);
