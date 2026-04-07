using System;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TotalRecall.Infrastructure.Embedding;

public sealed class OnnxEmbedder : IDisposable
{
    private readonly InferenceSession _session;

    public OnnxEmbedder(string modelPath)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, opts);
    }

    public float[] Embed(int[] tokenIds)
    {
        var len = tokenIds.Length;

        var inputIds = new DenseTensor<long>(new[] { 1, len });
        var attentionMask = new DenseTensor<long>(new[] { 1, len });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, len });
        for (var i = 0; i < len; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var results = _session.Run(inputs);
        var lastHidden = results[0].AsTensor<float>();
        // Shape: [1, len, 384]

        var pooled = new float[384];
        for (var i = 0; i < len; i++)
        {
            for (var d = 0; d < 384; d++)
                pooled[d] += lastHidden[0, i, d];
        }
        for (var d = 0; d < 384; d++)
            pooled[d] /= len;

        var sumSq = 0.0;
        for (var d = 0; d < 384; d++) sumSq += pooled[d] * pooled[d];
        var norm = (float)Math.Sqrt(sumSq);
        if (norm > 0)
            for (var d = 0; d < 384; d++) pooled[d] /= norm;

        return pooled;
    }

    public void Dispose() => _session.Dispose();
}
