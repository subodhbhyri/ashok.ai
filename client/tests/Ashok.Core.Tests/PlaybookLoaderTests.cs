using Ashok.Core.Contracts;
using Ashok.Core.Playbooks;

namespace Ashok.Core.Tests;

public class PlaybookLoaderTests
{
    private const string Valid = """
        id: find-downloads
        title: Find your downloaded files
        scope: system
        os: [win10, win11]
        examples:
          - where are my downloads
          - I can't find the file I downloaded
        steps:
          - id: s1
            say: Press the Windows key and E together to open File Explorer.
            action: pressKeys
            keys: Win+E
            doneWhen: { kind: windowOpened, process: explorer }
          - id: s2
            say: Click Downloads on the left side.
            action: click
            target: { role: TreeItem, name: Downloads }
            doneWhen: { kind: elementVisible, target: { role: Text, name: Downloads } }
        """;

    private static PlaybookException Fails(string yaml) =>
        Assert.Throws<PlaybookException>(() => PlaybookLoader.Parse(yaml, "test.yaml"));

    [Fact]
    public void A_valid_playbook_is_read_into_typed_records()
    {
        var playbook = PlaybookLoader.Parse(Valid, "find-downloads.yaml");

        Assert.Equal("find-downloads", playbook.Id);
        Assert.Equal(["win10", "win11"], playbook.Os);
        Assert.Equal(2, playbook.Steps.Count);

        var s1 = playbook.Steps[0];
        Assert.Equal(ActionKind.PressKeys, s1.Action);
        Assert.Equal("Win+E", s1.Keys);
        Assert.Equal(DoneKind.WindowOpened, s1.DoneWhen.Kind);
        Assert.Equal("explorer", s1.DoneWhen.Process);

        var s2 = playbook.Steps[1];
        Assert.Equal(new Target(null, "TreeItem", "Downloads", null), s2.Target);
        Assert.Equal(DoneKind.ElementVisible, s2.DoneWhen.Kind);
        Assert.Equal("Text", s2.DoneWhen.Target!.Role);
    }

    [Fact]
    public void An_unknown_doneWhen_kind_is_rejected() =>
        Assert.Contains("kind", Fails(Valid.Replace("kind: windowOpened", "kind: magic")).Message, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void A_ref_in_a_playbook_target_is_rejected()
    {
        var yaml = Valid.Replace("target: { role: TreeItem, name: Downloads }", "target: { ref: e4, role: TreeItem, name: Downloads }");

        Fails(yaml);
    }

    [Fact]
    public void A_click_without_a_target_is_rejected()
    {
        // Regex because the raw string takes the source file's line endings.
        var yaml = System.Text.RegularExpressions.Regex.Replace(
            Valid, @"^ *target: \{ role: TreeItem.*\r?\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);


        Fails(yaml);
    }

    [Fact]
    public void An_unsupported_os_is_rejected() => Fails(Valid.Replace("[win10, win11]", "[macos]"));

    [Fact]
    public void Duplicate_step_ids_are_rejected()
    {
        var yaml = Valid.Replace("id: s2", "id: s1");

        Assert.Contains("s1", Fails(yaml).Message);
    }

    [Fact]
    public void Missing_examples_are_rejected() => Fails(Valid.Replace("examples:", "extra:"));

    [Fact]
    public void Broken_yaml_is_reported_with_the_file_name()
    {
        var error = Fails("id: [unclosed");

        Assert.Equal("test.yaml", error.File);
    }

    [Fact]
    public void A_file_that_is_not_a_mapping_is_rejected() => Fails("- just\n- a list");

    [Fact]
    public void The_store_rejects_duplicate_ids()
    {
        var a = PlaybookLoader.Parse(Valid, "a.yaml");

        Assert.Throws<PlaybookException>(() => new PlaybookStore([a, a]));
    }

    [Fact]
    public void The_store_filters_by_os_and_finds_by_id()
    {
        var dir = Directory.CreateTempSubdirectory("ashok-playbooks").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "both.yaml"), Valid);
            File.WriteAllText(Path.Combine(dir, "w10.yaml"),
                Valid.Replace("find-downloads", "only-ten").Replace("[win10, win11]", "[win10]"));

            var win11 = PlaybookStore.LoadDirectory(dir, "win11");

            Assert.Equal(["find-downloads"], win11.All.Select(p => p.Id));
            Assert.NotNull(win11.Find("find-downloads"));
            Assert.Null(win11.Find("only-ten"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
