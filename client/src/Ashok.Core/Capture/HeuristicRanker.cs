using System.Text.RegularExpressions;
using Ashok.Core.Contracts;

namespace Ashok.Core.Capture;

/// <summary>
/// Chooses which candidates the planner sees. Three filters always run; a score is used only when
/// more than <c>limit</c> elements survive them.
/// <list type="number">
/// <item>Noise: icon-font glyphs, elements with neither a name nor an automation id, framework
/// template names ("BreadcrumbBarItemButton"), scrollbar parts, and Explorer's per-item property cells.</item>
/// <item>Exact duplicates (same role, name and bounds), which UIA reports for wrapped controls.</item>
/// <item>Redundant labels: Text inside an interactive element or repeating a control's name, and Edit
/// cells inside list or tree items. The control already carries that information.</item>
/// </list>
/// Output stays in document order, so the planner reads the window top to bottom, and refs are unchanged.
/// Names are cleaned of invisible bidi marks and repeated whitespace.
/// </summary>
public sealed partial class HeuristicRanker : IElementRanker
{
    private static readonly HashSet<string> Interactive = new(StringComparer.Ordinal)
    {
        "Button", "SplitButton", "CheckBox", "RadioButton", "ComboBox", "Hyperlink",
        "MenuItem", "TabItem", "ListItem", "TreeItem", "Slider", "Edit",
    };

    private static readonly HashSet<string> WindowChromeIds = new(StringComparer.Ordinal)
    {
        "Minimize", "Maximize", "Close", "Minimize-Restore", "Maximize-Restore",
    };

    // A control taller than this multiple of the text's own height is a region (for example a document
    // Edit), not a labelled control, so Text inside it is kept.
    private const int MaxContainerHeightRatio = 4;

    // Zero-width space, LRM/RLM, bidi embeddings/overrides (U+202A-202E), bidi isolates (U+2066-2069), BOM.
    [GeneratedRegex(@"[​‎‏‪-‮⁦-⁩﻿]")]
    private static partial Regex InvisibleMarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A single PascalCase token such as "BreadcrumbBarItemButton" or "ChevronTextBlock".
    [GeneratedRegex(@"^[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+$")]
    private static partial Regex PascalToken();

    [GeneratedRegex(@"(Button|TextBlock|Presenter|Panel)$")]
    private static partial Regex TemplateSuffix();

    [GeneratedRegex(@"^(Vertical|Horizontal)(Small|Large)(Increase|Decrease)$")]
    private static partial Regex ScrollbarPart();

    public RankResult Rank(IReadOnlyList<UiElement> candidates, int limit)
    {
        var items = new List<Item>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var cleaned = candidates[i] with { Name = Clean(candidates[i].Name) };
            if (!IsNoise(cleaned)) items.Add(new Item(cleaned, i));
        }

        items = Deduplicate(items);

        var controls = items.Where(x => Interactive.Contains(x.Element.Role)).ToList();
        items = items.Where(x => !IsRedundant(x.Element, controls)).ToList();

        if (items.Count <= limit)
            return new RankResult(items.Select(x => x.Element).ToList(), false);

        var kept = items
            .OrderByDescending(x => Score(x.Element))
            .ThenBy(x => x.Index)
            .Take(Math.Max(limit, 0))
            .OrderBy(x => x.Index)
            .Select(x => x.Element)
            .ToList();
        return new RankResult(kept, true);
    }

    private sealed record Item(UiElement Element, int Index);

    internal static string Clean(string name) =>
        Whitespace().Replace(InvisibleMarks().Replace(name, ""), " ").Trim();

    private static bool IsNoise(UiElement e)
    {
        var id = e.AutomationId ?? "";
        if (e.Name.Length == 0 && id.Length == 0) return true;
        if (IsIconGlyph(e.Name)) return true;
        if (ScrollbarPart().IsMatch(id)) return true;
        if (e.Role == "Edit" && id.StartsWith("System.", StringComparison.Ordinal)) return true;
        if (PascalToken().IsMatch(e.Name) && (e.Name == id || TemplateSuffix().IsMatch(e.Name))) return true;
        return false;
    }

    // Segoe Fluent / MDL2 icons live in the Private Use Area (U+E000-U+F8FF) and mean nothing to the planner.
    private static bool IsIconGlyph(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (!char.IsWhiteSpace(c) && (c < '' || c > '')) return false;
        return true;
    }

    private static List<Item> Deduplicate(List<Item> items)
    {
        var result = new List<Item>(items.Count);
        var slots = new Dictionary<(string Role, string Name, int X, int Y, int W, int H), int>();
        foreach (var item in items)
        {
            var e = item.Element;
            var key = (e.Role, e.Name.ToLowerInvariant(), e.Bounds[0], e.Bounds[1], e.Bounds[2], e.Bounds[3]);
            if (slots.TryGetValue(key, out var at))
            {
                // Keep the earlier position, but prefer the copy that has an automation id.
                if (result[at].Element.AutomationId is null && e.AutomationId is not null)
                    result[at] = result[at] with { Element = e };
                continue;
            }
            slots[key] = result.Count;
            result.Add(item);
        }
        return result;
    }

    private static bool IsRedundant(UiElement e, List<Item> controls)
    {
        if (e.Role == "Text")
        {
            foreach (var c in controls)
            {
                var control = c.Element;
                if (string.Equals(control.Name, e.Name, StringComparison.OrdinalIgnoreCase)) return true;
                if (control.Bounds[3] <= MaxContainerHeightRatio * e.Bounds[3] && Inside(e.Bounds, control.Bounds))
                    return true;
            }
            return false;
        }

        if (e.Role == "Edit")
        {
            foreach (var c in controls)
            {
                var role = c.Element.Role;
                if ((role == "ListItem" || role == "TreeItem") && Inside(e.Bounds, c.Element.Bounds)) return true;
            }
        }
        return false;
    }

    private static bool Inside(int[] inner, int[] outer, int tolerance = 2) =>
        inner[0] >= outer[0] - tolerance &&
        inner[1] >= outer[1] - tolerance &&
        inner[0] + inner[2] <= outer[0] + outer[2] + tolerance &&
        inner[1] + inner[3] <= outer[1] + outer[3] + tolerance;

    /// <summary>Only used when more than <c>limit</c> elements survive the filters.</summary>
    internal static double Score(UiElement e)
    {
        var score = Interactive.Contains(e.Role) ? 3.0 : 1.0;
        if (e.Toggle is not null) score += 1;
        if (!e.Enabled) score -= 1;
        if (e.Name.Length == 0) score -= 2;
        if (WindowChromeIds.Contains(e.AutomationId ?? "") || (e.Role == "MenuItem" && e.Name == "System")) score -= 1;
        return score;
    }
}
