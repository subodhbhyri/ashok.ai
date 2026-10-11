using Ashok.Core.Capture;
using Ashok.Core.Contracts;

namespace Ashok.Core.Tests;

public class HeuristicRankerTests
{
    private static readonly HeuristicRanker Ranker = new();
    private static int _next;

    private static UiElement El(
        string role, string name, int x = 0, int y = 0, int w = 100, int h = 30,
        string? id = null, bool enabled = true, string? toggle = null) =>
        new($"e{Interlocked.Increment(ref _next)}", role, name, id, [x, y, w, h], enabled, toggle);

    private static IReadOnlyList<UiElement> Keep(params UiElement[] elements) => Ranker.Rank(elements, 150).Elements;

    private static string[] Refs(IReadOnlyList<UiElement> elements) => elements.Select(e => e.Ref).ToArray();

    [Fact]
    public void Icon_font_glyphs_are_dropped() =>
        Assert.Empty(Keep(El("Text", ""), El("Text", " ", x: 300)));

    [Fact]
    public void Unnamed_elements_need_an_automation_id()
    {
        var withId = El("Button", "", x: 300, id: "QueryButton");

        Assert.Equal(new[] { withId.Ref }, Refs(Keep(El("Button", ""), withId)));
    }

    [Fact]
    public void Framework_template_names_are_dropped_but_real_pascal_names_are_kept()
    {
        var real = El("Button", "OneDrive", x: 900);

        var kept = Keep(
            El("Button", "BreadcrumbBarItemButton", id: "PART_ItemButton"),
            El("Text", "ChevronTextBlock", x: 500, id: "PART_ChevronTextBlock"),
            real);

        Assert.Equal(new[] { real.Ref }, Refs(kept));
    }

    [Fact]
    public void Exact_duplicates_collapse_to_the_copy_with_an_automation_id()
    {
        var plain = El("Edit", "Address Bar");
        var withId = El("Edit", "Address Bar", id: "TextBox");

        Assert.Equal(new[] { withId.Ref }, Refs(Keep(plain, withId)));
    }

    [Fact]
    public void Labels_inside_a_control_are_dropped()
    {
        var toggle = El("Button", "Swift Pair", w: 72, h: 40, toggle: "off");

        var kept = Keep(toggle, El("Text", "Off", x: 2, y: 10, w: 20, h: 19));

        Assert.Equal(new[] { toggle.Ref }, Refs(kept));
    }

    [Fact]
    public void Text_inside_a_tall_region_is_kept()
    {
        var document = El("Edit", "Document", w: 800, h: 600);
        var line = El("Text", "Dear Ashok", x: 10, y: 10, w: 200, h: 19);

        Assert.Equal(new[] { document.Ref, line.Ref }, Refs(Keep(document, line)));
    }

    [Fact]
    public void Text_that_repeats_a_control_name_is_dropped()
    {
        var button = El("Button", "Use LE Audio", x: 900);

        Assert.Equal(new[] { button.Ref }, Refs(Keep(El("Text", "use le audio"), button)));
    }

    [Fact]
    public void Explorer_property_cells_and_scrollbar_parts_are_dropped()
    {
        var item = El("ListItem", "OS (C:)", w: 250, h: 52);

        var kept = Keep(
            item,
            El("Edit", "Space used", x: 600, id: "System.PercentFull"),
            El("Button", "Vertical Small Decrease", x: 900, id: "VerticalSmallDecrease"));

        Assert.Equal(new[] { item.Ref }, Refs(kept));
    }

    [Fact]
    public void Names_lose_invisible_marks_and_extra_spaces() =>
        Assert.Equal("42% used", Assert.Single(Keep(El("Text", "‎42%  used "))).Name);

    [Fact]
    public void Under_the_limit_nothing_is_cut_and_order_is_kept()
    {
        var header = El("Text", "Other devices");
        var connect = El("Button", "Connect", y: 100);
        var status = El("Text", "Not connected", y: 200);

        var result = Ranker.Rank([header, connect, status], 3);

        Assert.False(result.Truncated);
        Assert.Equal(new[] { header.Ref, connect.Ref, status.Ref }, Refs(result.Elements));
    }

    [Fact]
    public void Over_the_limit_toggles_and_controls_beat_text_and_disabled_buttons()
    {
        var description = El("Text", "Connect to supported devices quickly");
        var disabled = El("Button", "Paste", y: 100, enabled: false);
        var toggle = El("Button", "Swift Pair", y: 200, toggle: "off");
        var button = El("Button", "Copy", y: 300);

        var result = Ranker.Rank([description, disabled, toggle, button], 2);

        Assert.True(result.Truncated);
        Assert.Equal(new[] { toggle.Ref, button.Ref }, Refs(result.Elements));
    }

    [Fact]
    public void Window_chrome_ranks_below_page_controls()
    {
        var close = El("Button", "Close", id: "Close");
        var connect = El("Button", "Connect", y: 100);

        Assert.Equal(new[] { connect.Ref }, Refs(Ranker.Rank([close, connect], 1).Elements));
    }
}
