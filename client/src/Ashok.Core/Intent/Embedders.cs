using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Ashok.Core.Intent;

/// <summary>Turns text into a unit-length vector, so cosine similarity is a dot product.</summary>
public interface IEmbedder
{
    float[] Embed(string text);
}

/// <summary>
/// all-MiniLM-L6-v2 on ONNX Runtime (CPU): WordPiece tokenizer from vocab.txt, mean pooling over the
/// attention mask, L2 normalisation. The full-precision and quantized model files share this code.
/// </summary>
public sealed class MiniLmEmbedder : IEmbedder, IDisposable
{
    private const int MaxTokens = 128;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    public MiniLmEmbedder(string modelPath, string vocabPath)
    {
        var options = new SessionOptions { IntraOpNumThreads = 2 };
        _session = new InferenceSession(modelPath, options);
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
    }

    public float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, MaxTokens, out _, out _).Select(i => (long)i).ToArray();
        var length = ids.Length;

        var inputIds = new DenseTensor<long>(ids, [1, length]);
        var attention = new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length]);
        var tokenTypes = new DenseTensor<long>(new long[length], [1, length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
        };
        if (_session.InputMetadata.ContainsKey("token_type_ids"))
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes));

        using var results = _session.Run(inputs);
        var hidden = results.First().AsTensor<float>();   // [1, length, 384]
        var dimension = hidden.Dimensions[2];

        // Every token is real (no padding in a batch of one), so mean pooling is a plain average.
        var pooled = new float[dimension];
        for (var t = 0; t < length; t++)
            for (var d = 0; d < dimension; d++)
                pooled[d] += hidden[0, t, d];
        for (var d = 0; d < dimension; d++) pooled[d] /= length;

        return Vectors.Normalize(pooled);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// A deterministic bag-of-words embedder (hashed words, no model file) for tests and any machine without
/// the real model. It rewards shared words, which is enough to exercise the matcher's thresholds.
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    private const int Dimensions = 512;

    public float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in Words(text))
        {
            var hash = Fnv1a(word);
            var sign = (hash & 1) == 0 ? 1f : -1f;
            vector[(int)((hash >> 1) % Dimensions)] += sign;
        }
        return Vectors.Normalize(vector);
    }

    private static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    // Stable across runs, unlike string.GetHashCode.
    private static uint Fnv1a(string s)
    {
        var hash = 2166136261u;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }
}

public static class Vectors
{
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        if (norm == 0) return v;
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    /// <summary>Dot product; equals cosine similarity for unit vectors.</summary>
    public static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
