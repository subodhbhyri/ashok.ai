using Ashok.Core.Contracts;

namespace Ashok.Core.Capture;

public sealed record RankResult(IReadOnlyList<UiElement> Elements, bool Truncated);

public interface IElementRanker
{
    RankResult Rank(IReadOnlyList<UiElement> candidates, int limit);
}

/// <summary>Baseline for tests: keep document order, cut at the limit. Production uses <see cref="HeuristicRanker"/>.</summary>
public sealed class DocumentOrderRanker : IElementRanker
{
    public RankResult Rank(IReadOnlyList<UiElement> candidates, int limit) =>
        candidates.Count <= limit
            ? new RankResult(candidates, false)
            : new RankResult(candidates.Take(limit).ToList(), true);
}
