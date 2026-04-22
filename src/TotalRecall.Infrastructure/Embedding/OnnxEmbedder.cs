using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// ONNX-runtime backed text embedder. Lazily loads the ONNX model and the
/// WordPiece vocabulary on first use, then runs inference, mean-pools over
/// the sequence dimension, and L2-normalizes the result.
///
/// Ports <c>src-ts/embedding/embedder.ts</c>.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private const string ModelFileName = "model.onnx";
    private const string TokenizerFileName = "tokenizer.json";

    private readonly ModelManager _modelManager;
    private readonly string _modelName;
    private readonly object _loadLock = new();

    private InferenceSession? _session;
    private FSharpMap<string, int>? _vocab;
    private ModelSpec? _spec;

    public OnnxEmbedder(ModelManager modelManager, string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelManager);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name must be provided.", nameof(modelName));
        }
        _modelManager = modelManager;
        _modelName = modelName;
    }

    /// <summary>
    /// True once the ONNX session and vocab have been loaded. Exposed for
    /// tests that verify laziness.
    /// </summary>
    public bool IsLoaded => _session is not null && _vocab is not null;

    /// <inheritdoc />
    public EmbedderDescriptor Descriptor
    {
        get
        {
            // Resolve dimensions/revision from the registry without loading
            // the ONNX session. If the model isn't in the registry we fall
            // back to a zero-dim descriptor; the fingerprint guard will
            // treat that as a mismatch against any previously-stamped
            // fingerprint and fail loudly rather than silently.
            var registry = _modelManager.Registry;
            if (registry.TryGetSpec(_modelName, out var spec) && spec is not null)
            {
                return new EmbedderDescriptor(
                    Provider: "local",
                    Model: _modelName,
                    Revision: spec.Revision ?? string.Empty,
                    Dimensions: spec.Dimensions);
            }
            return new EmbedderDescriptor("local", _modelName, string.Empty, 0);
        }
    }

    /// <inheritdoc />
    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureLoaded();

        // Tokenize via F# Core. Tokenizer.tokenize returns an F# int list
        // with CLS/SEP already prepended/appended and truncated to 512.
        var tokenList = Tokenizer.tokenize(_vocab!, text);
        int[] tokenIds = ListModule.ToArray(tokenList);
        int seqLen = tokenIds.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });
        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1L;
            tokenTypeIds[0, i] = 0L;
        }

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session!.Run(inputs);
        var lastHidden = results[0].AsTensor<float>(); // [1, seqLen, hiddenSize]

        int hiddenSize = _spec!.Dimensions;
        var pooled = new float[hiddenSize];
        for (int i = 0; i < seqLen; i++)
        {
            for (int d = 0; d < hiddenSize; d++)
            {
                pooled[d] += lastHidden[0, i, d];
            }
        }
        for (int d = 0; d < hiddenSize; d++)
        {
            pooled[d] /= seqLen;
        }

        double sumSq = 0.0;
        for (int d = 0; d < hiddenSize; d++)
        {
            sumSq += pooled[d] * pooled[d];
        }
        float norm = (float)Math.Sqrt(sumSq);
        if (norm > 0f)
        {
            for (int d = 0; d < hiddenSize; d++)
            {
                pooled[d] /= norm;
            }
        }

        return pooled;
    }

    private void EnsureLoaded()
    {
        if (IsLoaded)
        {
            return;
        }

        lock (_loadLock)
        {
            if (IsLoaded)
            {
                return;
            }

            var modelDir = _modelManager.EnsureModelAvailable(_modelName);
            var spec = _modelManager.Registry.GetSpec(_modelName);

            var onnxPath = Path.Combine(modelDir, ModelFileName);
            var tokenizerPath = Path.Combine(modelDir, TokenizerFileName);

            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            var session = new InferenceSession(onnxPath, opts);
            var vocab = LoadVocab(tokenizerPath);

            _session = session;
            _vocab = vocab;
            _spec = spec;
        }
    }

    private static FSharpMap<string, int> LoadVocab(string tokenizerJsonPath)
    {
        if (!File.Exists(tokenizerJsonPath))
        {
            throw new FileNotFoundException(
                $"tokenizer.json not found at {tokenizerJsonPath}",
                tokenizerJsonPath);
        }

        string json = File.ReadAllText(tokenizerJsonPath);

        TokenizerFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, TokenizerJsonContext.Default.TokenizerFileDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Failed to parse tokenizer.json at {tokenizerJsonPath}: {ex.Message}",
                ex);
        }

        if (dto?.Model?.Vocab is null)
        {
            throw new InvalidDataException(
                $"tokenizer.json at {tokenizerJsonPath} is missing model.vocab.");
        }

        // Build FSharpMap<string,int> from the dictionary. MapModule.OfSeq
        // consumes a sequence of System.Tuple<TKey, TValue>.
        var pairs = dto.Model.Vocab.Select(kv => new Tuple<string, int>(kv.Key, kv.Value));
        return MapModule.OfSeq(pairs);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _vocab = null;
        _spec = null;
    }
}

// ---------- tokenizer.json DTOs + source-gen JSON context ----------

internal sealed class TokenizerFileDto
{
    [JsonPropertyName("model")]
    public TokenizerModelDto? Model { get; set; }
}

internal sealed class TokenizerModelDto
{
    [JsonPropertyName("vocab")]
    public Dictionary<string, int>? Vocab { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(TokenizerFileDto))]
internal sealed partial class TokenizerJsonContext : JsonSerializerContext
{
}
