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
    // Tuned on the Model-test accuracy set with all-MiniLM-L6-v2: accuracy peaks at 0.50 (96.4% overall) and
    // is flat for margins 0 to 0.04. Provisional, since the set is small (40 phrasings, 15 non-matches);
    // revisit when real users' questions are available.
    public const double DefaultThreshold = 0.50;
    public const double DefaultMargin = 0.02;

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
        var avoided = AvoidedPlaybooks(question);
        return _index.Value
            .Where(entry => !avoided.Contains(entry.PlaybookId))
            .GroupBy(entry => entry.PlaybookId)
            .Select(group => new PlaybookScore(group.Key, group.Max(entry => Vectors.Dot(vector, entry.Vector))))
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    /// <summary>Playbooks the question asks the opposite of ("turn OFF bluetooth"): they are skipped, not just ranked lower.</summary>
    private HashSet<string> AvoidedPlaybooks(string question)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(question, @"[\p{L}\p{N}']+")
            .Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _playbooks
            .Where(p => p.AvoidWords is { } avoid && avoid.Any(words.Contains))
            .Select(p => p.Id)
            .ToHashSet();
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
    public const string Vocab = "vocab.txt";

    // int8 is the app's model: on the Model-test set (55 new phrasings plus the 50 playbook examples, at the
    // default threshold) it makes exactly the same decisions as fp32 (105 of 105, 98.1% each; best-score
    // difference median 0.001, max 0.045) at a quarter of the size (23 MB against 90 MB). fp32 stays as the
    // comparison baseline, so a model change that breaks the equivalence shows up in CI.
    public const string Default = Qint8;

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

    /// <summary>The default model from a models directory.</summary>
    public static MiniLmEmbedder OpenDefault(string directory) =>
        new(Path.Combine(directory, Default), Path.Combine(directory, Vocab));
}
