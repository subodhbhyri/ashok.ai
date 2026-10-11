using System.Text.Json;
using Ashok.Core.Capture;
using Ashok.Core.Contracts;

namespace Ashok.Core.Tests;

public class RedactionTests
{
    private static RawElement Raw(
        string role = "Button", string name = "OK", int w = 10, int h = 10,
        bool password = false, bool offscreen = false, string? toggle = null) =>
        new(role, name, null, 5, 6, w, h, true, toggle, password, offscreen);

    [Fact]
    public void Password_fields_are_dropped_and_counted()
    {
        var r = Redaction.Apply([Raw(password: true), Raw(), Raw(password: true)]);

        Assert.Single(r.Candidates);
        Assert.Equal(2, r.Redacted);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(0, 0)]
    public void Zero_size_elements_are_dropped(int w, int h) =>
        Assert.Empty(Redaction.Apply([Raw(w: w, h: h)]).Candidates);

    [Fact]
    public void Offscreen_elements_are_dropped() =>
        Assert.Empty(Redaction.Apply([Raw(offscreen: true)]).Candidates);

    [Fact]
    public void Text_longer_than_200_is_dropped_but_200_is_kept()
    {
        var r = Redaction.Apply([
            Raw("Text", new string('a', 201)),
            Raw("Text", new string('b', 200)),
        ]);

        var kept = Assert.Single(r.Candidates);
        Assert.Equal(Redaction.MaxNameLength, kept.Name.Length);
    }

    [Fact]
    public void Long_non_text_names_are_truncated_not_dropped()
    {
        var r = Redaction.Apply([Raw("Button", new string('x', 500))]);

        Assert.Equal(80, Assert.Single(r.Candidates).Name.Length);
    }

    [Theory]
    [InlineData("Text")]
    [InlineData("Edit")]
    public void Classic_list_cells_are_dropped_because_their_name_is_their_value(string role)
    {
        // Environment Variables dialog: a variable's value (or a PATH entry) is the element's name.
        var secretValue = new RawElement(role, "C:/Users/someone/secret-token-123", "ListViewSubItem-1", 5, 6, 10, 10, true, null, false, false);
        var button = Raw("Button", "Edit...");

        var result = Redaction.Apply([secretValue, button]);

        Assert.Equal("Edit...", Assert.Single(result.Candidates).Name);
        Assert.Equal(1, result.Redacted);
    }

    [Fact]
    public void List_items_keep_their_names_so_rows_can_still_be_targeted()
    {
        var row = new RawElement("ListItem", "Path", "ListViewItem-5", 5, 6, 10, 10, true, null, false, false);

        Assert.Single(Redaction.Apply([row]).Candidates);
    }

    [Fact]
    public void Email_addresses_are_masked_in_names() =>
        Assert.Equal("Subodh Bhyri [email]",
            Assert.Single(Redaction.Apply([Raw(name: "Subodh Bhyri someone@example.com")]).Candidates).Name);

    [Fact]
    public void Emails_are_masked_before_truncation()
    {
        var name = new string('a', 70) + " first.last+tag@mail.example.co.uk";

        var cut = Assert.Single(Redaction.Apply([Raw(name: name)]).Candidates).Name;

        Assert.DoesNotContain("@", cut);
        Assert.DoesNotContain("first", cut.Replace("a", ""));
    }

    [Fact]
    public void Truncation_does_not_split_a_surrogate_pair()
    {
        var name = new string('a', 79) + "\U0001F600" + "tail"; // emoji straddles index 80

        var cut = Assert.Single(Redaction.Apply([Raw(name: name)]).Candidates).Name;

        Assert.Equal(79, cut.Length);
    }

    [Fact]
    public void Refs_are_sequential_and_skip_nothing_for_dropped_elements()
    {
        var r = Redaction.Apply([Raw(), Raw(password: true), Raw(w: 0), Raw()]);

        Assert.Equal(["e1", "e2"], r.Candidates.Select(c => c.Ref));
    }

    [Fact]
    public void Bounds_toggle_and_empty_automation_id_are_mapped()
    {
        var e = Assert.Single(Redaction.Apply([Raw(toggle: "on")]).Candidates);

        Assert.Equal([5, 6, 10, 10], e.Bounds);
        Assert.Equal("on", e.Toggle);
        Assert.Null(e.AutomationId);
    }

    [Theory]
    [InlineData("KeePass")]
    [InlineData("keepass.exe")]
    [InlineData("1PASSWORD")]
    [InlineData("Bitwarden.EXE")]
    public void Sensitive_processes_match_case_insensitively_with_or_without_exe(string name) =>
        Assert.True(Redaction.IsSensitiveProcess(name));

    [Theory]
    [InlineData("explorer")]
    [InlineData("chrome")]
    [InlineData("keepassx-notes")]
    public void Other_processes_are_not_sensitive(string name) =>
        Assert.False(Redaction.IsSensitiveProcess(name));
}

public class RankerTests
{
    private static UiElement El(int i) => new($"e{i}", "Button", $"b{i}", null, [0, 0, 1, 1], true, null);

    [Fact]
    public void Under_the_limit_is_untouched()
    {
        var input = Enumerable.Range(1, 150).Select(El).ToList();

        var r = new DocumentOrderRanker().Rank(input, 150);

        Assert.Equal(150, r.Elements.Count);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Over_the_limit_keeps_document_order_and_sets_truncated()
    {
        var input = Enumerable.Range(1, 400).Select(El).ToList();

        var r = new DocumentOrderRanker().Rank(input, 150);

        Assert.Equal(150, r.Elements.Count);
        Assert.True(r.Truncated);
        Assert.Equal("e1", r.Elements[0].Ref);
        Assert.Equal("e150", r.Elements[^1].Ref);
    }
}

public class SnapshotSerializationTests
{
    private static JsonElement Roundtrip(UiSnapshot s) => JsonDocument.Parse(SnapshotJson.Serialize(s)).RootElement;

    private static UiSnapshot Sample(params UiElement[] elements) =>
        new(new OsInfo("Windows 11", "22631"), new ForegroundInfo("SystemSettings", "Settings"), elements, 1, false);

    [Fact]
    public void Serialized_snapshot_validates_against_the_schema()
    {
        var snapshot = Sample(
            new UiElement("e1", "ListItem", "Bluetooth & devices", "SettingsPageGroupDevices", [24, 310, 260, 40], true, null),
            new UiElement("e2", "Button", "Bluetooth", null, [400, 200, 60, 30], true, "off"));

        Assert.True(ContractSchemas.Get("ui-snapshot").Evaluate(Roundtrip(snapshot)).IsValid);
    }

    [Fact]
    public void Empty_snapshot_validates_against_the_schema() =>
        Assert.True(ContractSchemas.Get("ui-snapshot").Evaluate(Roundtrip(Sample())).IsValid);

    [Fact]
    public void Toggle_null_is_written_explicitly_and_empty_automation_id_is_omitted()
    {
        var json = Roundtrip(Sample(new UiElement("e1", "Button", "OK", null, [0, 0, 1, 1], true, null)));

        var el = json.GetProperty("elements")[0];
        Assert.Equal(JsonValueKind.Null, el.GetProperty("toggle").ValueKind);
        Assert.False(el.TryGetProperty("automationId", out _));
    }
}
