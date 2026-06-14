using System.Collections.Generic;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>Dictionary-backed IMetaStore for fingerprint tests.</summary>
public sealed class InMemoryMetaStore : IMetaStore
{
    private readonly Dictionary<string, string> _kv = new();
    public string? GetMeta(string key) => _kv.TryGetValue(key, out var v) ? v : null;
    public void SetMeta(string key, string value) => _kv[key] = value;
    public void DeleteMeta(string key) => _kv.Remove(key);
}
