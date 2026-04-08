using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Resolves and validates model directories on disk. Ports the lookup and
/// validation subset of <c>src-ts/embedding/model-manager.ts</c>. Download
/// logic is intentionally deferred (Task 3.8+).
/// </summary>
public sealed class ModelManager
{
    private const string ModelOnnxFileName = "model.onnx";
    private const string VerifiedSidecarFileName = ".verified";

    private readonly ModelRegistry _registry;
    private readonly string _bundledBaseDir;
    private readonly string _userBaseDir;

    public ModelManager(ModelRegistry registry, string bundledBaseDir, string userBaseDir)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _bundledBaseDir = bundledBaseDir ?? throw new ArgumentNullException(nameof(bundledBaseDir));
        _userBaseDir = userBaseDir ?? throw new ArgumentNullException(nameof(userBaseDir));
    }

    /// <summary>
    /// Path to the bundled copy of the named model (may or may not exist).
    /// </summary>
    public string GetBundledModelPath(string modelName) =>
        Path.Combine(_bundledBaseDir, modelName);

    /// <summary>
    /// Path to the user-override copy of the named model (may or may not exist).
    /// </summary>
    public string GetUserModelPath(string modelName) =>
        Path.Combine(_userBaseDir, modelName);

    /// <summary>
    /// Returns the bundled path if it is structurally valid for the spec,
    /// otherwise falls back to the user path. If the model name is unknown
    /// in the registry the user path is returned (callers handle missing).
    /// </summary>
    public string GetModelPath(string modelName)
    {
        if (!_registry.TryGetSpec(modelName, out var spec) || spec is null)
        {
            return GetUserModelPath(modelName);
        }

        var bundled = GetBundledModelPath(modelName);
        if (IsStructurallyValid(bundled, spec))
        {
            return bundled;
        }

        return GetUserModelPath(modelName);
    }

    /// <summary>
    /// Structural validation: directory exists, every file listed in
    /// <c>spec.Files</c> exists, and <c>model.onnx</c> matches the declared
    /// byte size. The size check implicitly catches LFS pointer files.
    /// </summary>
    public bool IsStructurallyValid(string modelPath, ModelSpec spec)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrEmpty(modelPath)) return false;

        if (!Directory.Exists(modelPath))
        {
            return false;
        }

        foreach (var fileName in spec.Files.Keys)
        {
            var candidate = Path.Combine(modelPath, fileName);
            if (!File.Exists(candidate))
            {
                return false;
            }
        }

        var onnxPath = Path.Combine(modelPath, ModelOnnxFileName);
        if (!File.Exists(onnxPath))
        {
            return false;
        }

        var actualSize = new FileInfo(onnxPath).Length;
        if (actualSize != spec.SizeBytes)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checksum validation: if a <c>.verified</c> sidecar is present and
    /// matches the expected SHA-256, returns true immediately (cache hit).
    /// Otherwise hashes <c>model.onnx</c>, writes the sidecar on match, and
    /// returns whether the hashes agree.
    /// </summary>
    public bool IsChecksumValid(string modelPath, ModelSpec spec)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        if (string.IsNullOrEmpty(modelPath)) return false;

        var onnxPath = Path.Combine(modelPath, ModelOnnxFileName);
        if (!File.Exists(onnxPath))
        {
            return false;
        }

        var sidecarPath = Path.Combine(modelPath, VerifiedSidecarFileName);
        if (File.Exists(sidecarPath))
        {
            var cached = File.ReadAllText(sidecarPath).Trim();
            if (string.Equals(cached, spec.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string actualHash;
        using (var stream = File.OpenRead(onnxPath))
        {
            var hashBytes = SHA256.HashData(stream);
            actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        if (!string.Equals(actualHash, spec.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            File.WriteAllText(sidecarPath, spec.Sha256);
        }
        catch (IOException)
        {
            // Non-fatal: hash matched, caching failed (read-only dir, etc.).
        }
        catch (UnauthorizedAccessException)
        {
            // Non-fatal.
        }

        return true;
    }

    /// <summary>
    /// Convenience: looks up the spec, resolves the path, and throws with a
    /// clear message if the directory is not structurally valid. Does NOT
    /// perform the expensive checksum verification.
    /// </summary>
    public string EnsureModelAvailable(string modelName)
    {
        var spec = _registry.GetSpec(modelName);
        var path = GetModelPath(modelName);

        if (!IsStructurallyValid(path, spec))
        {
            var expected = string.Join(", ", spec.Files.Keys.OrderBy(k => k, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Model '{modelName}' not found at {path}. " +
                $"Expected files: {expected}. " +
                $"See docs for setup.");
        }

        return path;
    }
}
