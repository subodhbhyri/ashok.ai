using Ashok.Core.Contracts;

namespace Ashok.Core.Capture;

public sealed record RankResult(IReadOnlyList<UiElement> Elements, bool Truncated);

// OPUS: element ranking and trimming. Decide which <= limit of the candidates (often thousands) matter
// for planning: plan quality and token cost both depend on it. Inputs are post-redaction candidates in
// document order with refs already assigned; see `Ashok.DevTool snapshot --raw` for real candidate sets.
public interface IElementRanker
{
    RankResult Rank(IReadOnlyList<UiElement> candidates, int limit);
}

/// <summary>Placeholder until the real heuristic lands: keep document order, cut at the limit.</summary>
public sealed class DocumentOrderRanker : IElementRanker
{
    public RankResult Rank(IReadOnlyList<UiElement> candidates, int limit) =>
        candidates.Count <= limit
            ? new RankResult(candidates, false)
            : new RankResult(candidates.Take(limit).ToList(), true);
}
