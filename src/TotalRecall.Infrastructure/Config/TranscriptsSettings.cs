namespace TotalRecall.Infrastructure.Config;

/// <summary>
/// The <c>[transcripts]</c> settings of the opt-in transcript archive, read by
/// <see cref="ConfigLoader.LoadTranscriptsSettings"/>. <paramref name="DbPath"/> is
/// empty for "the main database"; <paramref name="DbPathError"/> is non-null when
/// <c>db_path</c> is present but unusable (then <c>DbPath</c> is empty and the
/// caller treats the archive as unavailable).
/// </summary>
public sealed record TranscriptsSettings(
    bool Enabled,
    string DbPath,
    string? DbPathError,
    int RetentionDays,
    bool AllowRemoteEmbedding);
