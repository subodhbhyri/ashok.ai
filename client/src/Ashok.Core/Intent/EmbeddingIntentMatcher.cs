using Ashok.Core.Contracts;
using Ashok.Core.Session;

namespace Ashok.Core.Intent;

public sealed record PlaybookScore(string PlaybookId, double Score);

/// <summary>
/// Picks the playbook whose example phrases are closest to the question. Each playbook's score is its best
/// example; a match needs a high enough score and a clear lead over the runner-up, otherwise the answer is
/// "none" and the question goes to the planner.
/// </summary>
public sealed class EmbeddingIntentMatcher : IIntentMatcher
{
    // Provisional defaults; tuned on the accuracy set in the Model tests (see ModelMatcherTests).
    public const double DefaultThreshold = 0.55;
    public const double DefaultMargin = 0.03;

    private readonly IEmbedder _embedder;
    private readonly IReadOnlyList<Playbook> _playbooks;
    private readonly Lazy<IReadOnlyList<(string PlaybookId, float[] Vector)>> _index;

    public EmbeddingIntentMatcher(
        IEmbedder embedder, IEnumerable<Playbook> playbooks,
        double threshold = DefaultThreshold, double margin = DefaultMargin)
    {
        _embedder = embedder;
        _playbooks = playbooks.ToList();
        Threshold = threshold;
        Margin = margin;
        _index = new Lazy<IReadOnlyList<(string, float[])>>(BuildIndex);
    }

    public double Threshold { get; }

    public double Margin { get; }

    public IntentMatch? Match(string question)
    {
        var ranked = Rank(question);
        if (ranked.Count == 0) return null;

        var best = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1].Score : double.NegativeInfinity;
        if (best.Score < Threshold || best.Score - runnerUp < Margin) return null;
        return new IntentMatch(best.PlaybookId, best.Score);
    }

    /// <summary>Every playbook with its score, best first. Used for tuning and diagnostics.</summary>
    public IReadOnlyList<PlaybookScore> Rank(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return [];

        var vector = _embedder.Embed(question);
        return _index.Value
            .GroupBy(entry => entry.PlaybookId)
            .Select(group => new PlaybookScore(group.Key, group.Max(entry => Vectors.Dot(vector, entry.Vector))))
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    private IReadOnlyList<(string, float[])> BuildIndex()
    {
        var entries = new List<(string, float[])>();
        foreach (var playbook in _playbooks)
        {
            entries.Add((playbook.Id, _embedder.Embed(playbook.Title)));
            foreach (var example in playbook.Examples)
                entries.Add((playbook.Id, _embedder.Embed(example)));
        }
        return entries;
    }
}

/// <summary>Where the downloaded model files live (client/models by default, or ASHOK_MODEL_DIR).</summary>
public static class ModelFiles
{
    public const string Fp32 = "model_fp32.onnx";
    public const string Qint8 = "model_qint8.onnx";
    public const string Quint8 = "model_quint8_avx2.onnx";
    public const string Vocab = "vocab.txt";

    public static string? FindDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ASHOK_MODEL_DIR");
        if (!string.IsNullOrEmpty(fromEnvironment) && Directory.Exists(fromEnvironment)) return fromEnvironment;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "client", "models");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "models");
            if (File.Exists(Path.Combine(candidate, Vocab))) return candidate;
        }
        return null;
    }
}
