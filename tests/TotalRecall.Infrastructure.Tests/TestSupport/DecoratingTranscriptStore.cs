using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// Passes every call to a real store, except the embed members a test replaces through the hooks (for example to throw
/// SQLITE_BUSY without holding a real lock). Disposing does not dispose the inner store.
/// </summary>
public sealed class DecoratingTranscriptStore : ITranscriptStore
{
    private readonly ITranscriptStore _inner;

    public DecoratingTranscriptStore(ITranscriptStore inner) => _inner = inner;

    public Func<int, IReadOnlyCollection<long>, IReadOnlyList<EmbedChunk>>? OnNextEmbedWork { get; set; }
    public Func<IReadOnlyList<ChunkEmbedding>, ArchiveFingerprint, EmbedLeaseHolder, DateTimeOffset, EmbedBatchOutcome>? OnStoreEmbeddings { get; set; }
    public Action<EmbedRunRecord>? OnWriteEmbedRun { get; set; }
    public Action<EmbedLeaseHolder>? OnReleaseEmbedLease { get; set; }

    public int ReleaseCalls { get; private set; }
    public int WriteRunCalls { get; private set; }
    public int CountEmbeddingCalls { get; private set; }

    public ITranscriptStore Inner => _inner;

    public TranscriptStatus Status() => _inner.Status();
    public ForgetCount CountSession(string host, string hostSessionId) => _inner.CountSession(host, hostSessionId);
    public ForgetCount CountProject(string projectKey) => _inner.CountProject(projectKey);
    public ForgetCount CountAll() => _inner.CountAll();
    public ForgetResult ForgetSession(string host, string hostSessionId) => _inner.ForgetSession(host, hostSessionId);
    public ForgetResult ForgetProject(string projectKey) => _inner.ForgetProject(projectKey);
    public ForgetResult ForgetAll(DateTimeOffset now) => _inner.ForgetAll(now);
    public bool UnforgetSession(string host, string hostSessionId) => _inner.UnforgetSession(host, hostSessionId);
    public bool UnforgetProject(string projectKey) => _inner.UnforgetProject(projectKey);
    public bool ClearCutoff() => _inner.ClearCutoff();
    public bool IsExcluded(string host, string hostSessionId, string? projectKey, string? cwd) =>
        _inner.IsExcluded(host, hostSessionId, projectKey, cwd);
    public SessionIngestState? GetIngestState(string host, string hostSessionId) => _inner.GetIngestState(host, hostSessionId);
    public IngestCutoff? IngestNotBefore() => _inner.IngestNotBefore();
    public IngestOutcome ApplyIngest(SessionIngest ingest) => _inner.ApplyIngest(ingest);
    public bool IsBeforeCutoff(DateTimeOffset timestamp) => _inner.IsBeforeCutoff(timestamp);

    public IReadOnlyList<EmbedChunk> NextEmbedWork(int limit, IReadOnlyCollection<long> exclude) =>
        OnNextEmbedWork is { } f ? f(limit, exclude) : _inner.NextEmbedWork(limit, exclude);

    public EmbedBatchOutcome StoreEmbeddings(
        IReadOnlyList<ChunkEmbedding> batch, ArchiveFingerprint current, EmbedLeaseHolder holder, DateTimeOffset now) =>
        OnStoreEmbeddings is { } f ? f(batch, current, holder, now) : _inner.StoreEmbeddings(batch, current, holder, now);

    public bool RecordEmbedFailure(long chunkId, string content) => _inner.RecordEmbedFailure(chunkId, content);
    public int ResetEmbedAttempts() => _inner.ResetEmbedAttempts();
    public bool TryAcquireEmbedLease(EmbedLeaseHolder holder, DateTimeOffset now, Func<string, int, bool> holderIsAlive) =>
        _inner.TryAcquireEmbedLease(holder, now, holderIsAlive);

    public void ReleaseEmbedLease(EmbedLeaseHolder holder)
    {
        ReleaseCalls++;
        if (OnReleaseEmbedLease is { } f) f(holder);
        else _inner.ReleaseEmbedLease(holder);
    }

    public ArchiveFingerprint? ReadEmbedFingerprint() => _inner.ReadEmbedFingerprint();
    public FingerprintState CheckFingerprint(ArchiveFingerprint current) => _inner.CheckFingerprint(current);
    public EmbeddingCounts CountEmbedding()
    {
        CountEmbeddingCalls++;
        return _inner.CountEmbedding();
    }

    public void WriteEmbedRun(EmbedRunRecord run)
    {
        WriteRunCalls++;
        if (OnWriteEmbedRun is { } f) f(run);
        else _inner.WriteEmbedRun(run);
    }

    public EmbedRunRecord? ReadEmbedRun() => _inner.ReadEmbedRun();

    public Func<ITranscriptSearchReads>? OnBeginSearchReads { get; set; }

    public ITranscriptSearchReads BeginSearchReads() =>
        OnBeginSearchReads is { } f ? f() : _inner.BeginSearchReads();

    public void Dispose()
    {
    }
}
