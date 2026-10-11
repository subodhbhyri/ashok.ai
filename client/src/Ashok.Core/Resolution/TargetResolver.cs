using System.Text;
using Ashok.Core.Contracts;

namespace Ashok.Core.Resolution;

public enum MatchKind { AutomationId, ExactName, FuzzyName, RoleFallback }

/// <param name="Index">Position in the candidate list, i.e. document order.</param>
public sealed record Resolved(UiElement Element, double Score, MatchKind How, int Index);

public sealed record ResolveResult(IReadOnlyList<Resolved> Matches)
{
    public Resolved? Best => Matches.Count > 0 ? Matches[0] : null;

    /// <summary>True when the two best matches score the same, so picking one is a guess.</summary>
    public bool IsTie => Matches.Count > 1 && Math.Abs(Matches[0].Score - Matches[1].Score) < 1e-9;
}

/// <summary>
/// Finds a playbook or plan target in the live element list. It works on the unranked candidates
/// (post-redaction, pre-ranking): the ranker drops "redundant" elements such as a heading that repeats a
/// control's name, which is often exactly what a target points at.
/// </summary>
public sealed class TargetResolver
{
    /// <summary>Minimum name similarity (0..1) for a fuzzy match; below this nothing is returned.</summary>
    public const double FuzzyThreshold = 0.8;

    private const double CompatibleRoleFactor = 0.85;

    // Roles a user would treat as interchangeable "things to click". Text and Edit stand alone.
    private static readonly string[][] RoleGroups =
    [
        ["Button", "SplitButton", "Hyperlink", "MenuItem", "ListItem", "TreeItem", "TabItem"],
        ["CheckBox", "RadioButton", "Button"],
    ];

    /// <summary>All acceptable matches, best first (ties in document order). Empty when nothing is good enough.</summary>
    public ResolveResult Resolve(Target target, IReadOnlyList<UiElement> candidates)
    {
        var wanted = Normalize(target.Name);
        var matches = new List<Resolved>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var element = candidates[i];
            var roleFactor = RoleFactor(target.Role, element.Role);
            if (roleFactor == 0) continue;

            if (!string.IsNullOrEmpty(target.AutomationId) &&
                string.Equals(target.AutomationId, element.AutomationId, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new Resolved(element, 1.0 * roleFactor + 0.01, MatchKind.AutomationId, i));
                continue;
            }

            var name = Normalize(element.Name);
            if (name.Length == 0 || wanted.Length == 0) continue;

            if (name == wanted)
            {
                matches.Add(new Resolved(element, roleFactor, roleFactor == 1 ? MatchKind.ExactName : MatchKind.RoleFallback, i));
                continue;
            }

            var similarity = Similarity(wanted, name);
            if (similarity >= FuzzyThreshold)
                matches.Add(new Resolved(element, similarity * roleFactor, MatchKind.FuzzyName, i));
        }

        return new ResolveResult(matches.OrderByDescending(m => m.Score).ThenBy(m => m.Index).ToList());
    }

    public Resolved? Best(Target target, IReadOnlyList<UiElement> candidates) => Resolve(target, candidates).Best;

    private static double RoleFactor(string wanted, string actual)
    {
        if (string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase)) return 1.0;
        foreach (var group in RoleGroups)
            if (group.Contains(wanted, StringComparer.OrdinalIgnoreCase) && group.Contains(actual, StringComparer.OrdinalIgnoreCase))
                return CompatibleRoleFactor;
        return 0;
    }

    /// <summary>Lower-case letters and digits with single spaces; "&amp;" reads as "and"; punctuation vanishes ("Wi-Fi" is "wifi").</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s.Replace("&", " and "))
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
            // other punctuation is dropped without breaking the word
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whichever is higher of character-level similarity (typos, small wording changes) and word-set overlap
    /// (reordered words). Deliberately not prefix-friendly: "bluetooth" must not match "bluetooth and devices".
    /// </summary>
    internal static double Similarity(string a, string b)
    {
        var edit = 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);

        var wordsA = a.Split(' ').ToHashSet();
        var wordsB = b.Split(' ').ToHashSet();
        var shared = wordsA.Count(wordsB.Contains);
        var dice = 2.0 * shared / (wordsA.Count + wordsB.Count);

        return Math.Max(edit, dice);
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
