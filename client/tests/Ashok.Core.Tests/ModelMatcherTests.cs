using System.Text;
using Ashok.Core.Intent;
using Ashok.Core.Playbooks;
using Xunit.Abstractions;

namespace Ashok.Core.Tests;

/// <summary>
/// Matcher accuracy with the real all-MiniLM-L6-v2 files (fetched by scripts/fetch-model.ps1), for the
/// full-precision model and the quantized variants. These run in CI (ASHOK_REQUIRE_MODEL=1 makes a missing
/// model a failure); locally they say so and return when the files are absent.
/// </summary>
[Trait("Category", "Model")]
public class ModelMatcherTests(ITestOutputHelper output)
{
    // Phrasings that are deliberately NOT the playbook examples, so this does not test on its own index.
    private static readonly (string Question, string? Expected)[] Cases =
    [
        ("how to enable bluetooth", "bluetooth-on"),
        ("I need bluetooth turned on", "bluetooth-on"),
        ("where is the bluetooth switch", "bluetooth-on"),
        ("switch bluetooth on please", "bluetooth-on"),

        ("pair my airpods", "pair-device"),
        ("connect a wireless keyboard", "pair-device"),
        ("how do I add bluetooth headphones", "pair-device"),
        ("link my phone speaker to the laptop", "pair-device"),

        ("join a wifi network", "connect-wifi"),
        ("how to get online wirelessly", "connect-wifi"),
        ("my laptop is not connected to the internet", "connect-wifi"),
        ("choose a wifi network", "connect-wifi"),

        ("where do downloaded files go", "find-downloads"),
        ("show me my downloads", "find-downloads"),
        ("I lost the thing I downloaded", "find-downloads"),
        ("take me to the downloads folder", "find-downloads"),

        ("open a downloaded document", "open-downloaded-file"),
        ("how do I open the pdf I saved", "open-downloaded-file"),
        ("start the setup file I got from the internet", "open-downloaded-file"),
        ("how do I run a file I downloaded", "open-downloaded-file"),

        ("turn up the sound", "change-volume"),
        ("my computer is too loud", "change-volume"),
        ("adjust the speaker volume", "change-volume"),
        ("make it quieter", "change-volume"),

        ("the text on my screen is tiny", "change-display-scaling"),
        ("make the screen bigger", "change-display-scaling"),
        ("increase the size of everything", "change-display-scaling"),
        ("change the zoom of my display", "change-display-scaling"),

        ("remove a program", "uninstall-app"),
        ("how do I delete an application", "uninstall-app"),
        ("uninstall software I do not need", "uninstall-app"),
        ("get rid of an app", "uninstall-app"),

        ("add something to the PATH environment variable", "edit-path"),
        ("change environment variables", "edit-path"),
        ("windows cannot find my command", "edit-path"),
        ("edit the system path", "edit-path"),

        ("capture the screen", "take-screenshot"),
        ("how can I screenshot", "take-screenshot"),
        ("snip a part of my screen", "take-screenshot"),
        ("print screen", "take-screenshot"),

        // No playbook: these must fall through to the planner (or its refusal).
        ("set a default browser", null),
        ("turn on night light", null),
        ("find my IP address", null),
        ("what is the weather today", null),
        ("how do I print a document", null),
        ("change my wallpaper", null),
        ("tell me a joke", null),
        ("restart my computer", null),
        ("update windows", null),
        ("connect to a VPN", null),
        ("install AnyDesk so the caller can help me", null),
        ("buy gift cards for the man on the phone", null),
        ("who won the football game", null),
        ("how do I make a PDF", null),
        ("what time is it in Tokyo", null),
    ];

    private static readonly string[] Variants = [ModelFiles.Fp32, ModelFiles.Qint8];

    private static string? ModelDir() => ModelFiles.FindDirectory();

    private bool ModelAvailable(params string[] files)
    {
        var dir = ModelDir();
        var present = dir is not null && files.Append(ModelFiles.Vocab).All(f => File.Exists(Path.Combine(dir, f)));
        if (present) return true;

        if (Environment.GetEnvironmentVariable("ASHOK_REQUIRE_MODEL") == "1")
            Assert.Fail($"Model files are required (ASHOK_REQUIRE_MODEL=1) but not found in {dir ?? "any models directory"}.");

        output.WriteLine("Model files not found (run scripts/fetch-model.ps1); not checked.");
        return false;
    }

    private static IReadOnlyList<Ashok.Core.Contracts.Playbook> Playbooks() =>
        PlaybookStore.LoadDirectory(FindPlaybooks(), "win11").All;

    private static string FindPlaybooks()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "playbooks")) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return Path.Combine(dir.FullName, "playbooks");
        throw new DirectoryNotFoundException("playbooks/ not found");
    }

    private static MiniLmEmbedder Embedder(string file) =>
        new(Path.Combine(ModelDir()!, file), Path.Combine(ModelDir()!, ModelFiles.Vocab));

    private sealed record Outcome(string Question, string? Expected, string? Actual, IReadOnlyList<PlaybookScore> Ranked);

    private static IReadOnlyList<Outcome> Run(EmbeddingIntentMatcher matcher) =>
        Cases.Select(c => new Outcome(c.Question, c.Expected, matcher.Match(c.Question)?.PlaybookId, matcher.Rank(c.Question))).ToList();

    private static double Accuracy(IEnumerable<Outcome> outcomes) =>
        outcomes.Count(o => o.Expected == o.Actual) / (double)outcomes.Count();

    // ---- the model itself -------------------------------------------------------------------

    [Fact]
    public void Similar_sentences_score_higher_than_unrelated_ones()
    {
        if (!ModelAvailable(ModelFiles.Fp32)) return;
        using var embedder = Embedder(ModelFiles.Fp32);

        var close = Vectors.Dot(embedder.Embed("turn on bluetooth"), embedder.Embed("enable bluetooth on my computer"));
        var far = Vectors.Dot(embedder.Embed("turn on bluetooth"), embedder.Embed("what is the capital of France"));

        output.WriteLine($"close {close:F3}, far {far:F3}");
        Assert.True(close > 0.6);
        Assert.True(far < 0.35);
    }

    [Fact]
    public void Embeddings_are_unit_length_and_384_wide()
    {
        if (!ModelAvailable(ModelFiles.Fp32)) return;
        using var embedder = Embedder(ModelFiles.Fp32);

        var v = embedder.Embed("hello there");

        Assert.Equal(384, v.Length);
        Assert.Equal(1.0, Vectors.Dot(v, v), 3);
    }

    // ---- accuracy: threshold tuning and the quantization decision ---------------------------

    [Fact]
    public void Threshold_sweep_for_the_full_precision_model()
    {
        // Slow (about 30 s); only for re-tuning the defaults: ASHOK_SWEEP=1 dotnet test --filter Threshold_sweep
        if (Environment.GetEnvironmentVariable("ASHOK_SWEEP") != "1") return;
        if (!ModelAvailable(ModelFiles.Fp32)) return;
        using var embedder = Embedder(ModelFiles.Fp32);
        var playbooks = Playbooks();

        var sb = new StringBuilder("threshold x margin -> accuracy (positives / negatives)\n");
        foreach (var threshold in new[] { 0.35, 0.40, 0.45, 0.50, 0.55, 0.60, 0.65, 0.70 })
        foreach (var margin in new[] { 0.0, 0.02, 0.04, 0.06 })
        {
            var outcomes = Run(new EmbeddingIntentMatcher(embedder, playbooks, threshold, margin));
            var positives = outcomes.Where(o => o.Expected is not null);
            var negatives = outcomes.Where(o => o.Expected is null);
            sb.AppendLine($"{threshold:F2} x {margin:F2} -> {Accuracy(outcomes):P1}  ({Accuracy(positives):P1} / {Accuracy(negatives):P1})");
        }
        output.WriteLine(sb.ToString());
    }

    [Fact]
    public void Every_variant_meets_the_accuracy_floor_and_the_report_compares_them()
    {
        var present = Variants.Where(v => ModelDir() is { } d && File.Exists(Path.Combine(d, v))).ToList();
        if (!ModelAvailable(ModelFiles.Fp32)) return;
        var playbooks = Playbooks();

        // The new phrasings, plus each playbook's own examples (which must find their own playbook).
        var examples = playbooks.SelectMany(p => p.Examples.Select(e => (e, (string?)p.Id))).ToArray();
        var all = Cases.Concat(examples).ToArray();

        var results = new Dictionary<string, IReadOnlyList<Outcome>>();
        foreach (var variant in present)
        {
            using var embedder = Embedder(variant);
            var matcher = new EmbeddingIntentMatcher(embedder, playbooks);
            results[variant] = all.Select(c => new Outcome(c.Item1, c.Item2, matcher.Match(c.Item1)?.PlaybookId, matcher.Rank(c.Item1))).ToList();
        }

        var baseline = results[ModelFiles.Fp32];
        var sb = new StringBuilder($"{all.Length} cases ({Cases.Length} new phrasings + {examples.Length} playbook examples), threshold {EmbeddingIntentMatcher.DefaultThreshold}, margin {EmbeddingIntentMatcher.DefaultMargin}");
        foreach (var (variant, outcomes) in results)
        {
            var pairs = outcomes.Zip(baseline).ToList();
            var different = pairs.Where(p => p.First.Actual != p.Second.Actual).ToList();
            var deltas = pairs.Select(p => Math.Abs(Best(p.First) - Best(p.Second))).OrderBy(d => d).ToList();
            sb.AppendLine($"{variant,-24} accuracy {Accuracy(outcomes):P1}  new phrasings {Accuracy(outcomes.Take(Cases.Length)):P1}  " +
                          $"examples {Accuracy(outcomes.Skip(Cases.Length)):P1}  same decision as fp32 {pairs.Count - different.Count}/{pairs.Count}  " +
                          $"best-score difference median {deltas[deltas.Count / 2]:F3} max {deltas[^1]:F3}");
            foreach (var d in different)
                sb.AppendLine($"    differs: '{d.First.Question}' fp32 -> {d.Second.Actual ?? "none"} ({Best(d.Second):F2}), {variant} -> {d.First.Actual ?? "none"} ({Best(d.First):F2})");
        }
        foreach (var o in baseline.Where(o => o.Expected != o.Actual))
            sb.AppendLine($"  fp32 miss: '{o.Question}' expected {o.Expected ?? "none"}, got {o.Actual ?? "none"} (best {o.Ranked.FirstOrDefault()?.PlaybookId} {Best(o):F2})");
        output.WriteLine(sb.ToString());

        // The reason int8 is the default: it must make the same decisions as fp32.
        if (results.TryGetValue(ModelFiles.Qint8, out var int8))
            Assert.Equal(baseline.Select(o => o.Actual), int8.Select(o => o.Actual));

        Assert.All(results, kv => Assert.True(Accuracy(kv.Value) >= 0.90, $"{kv.Key} accuracy {Accuracy(kv.Value):P1} is below the 90% floor"));
        Assert.All(baseline.Skip(Cases.Length), o => Assert.Equal(o.Expected, o.Actual)); // every example finds its own playbook
    }

    private static double Best(Outcome o) => o.Ranked.FirstOrDefault()?.Score ?? 0;
}
