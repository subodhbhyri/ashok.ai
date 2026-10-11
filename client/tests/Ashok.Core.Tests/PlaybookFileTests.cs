using System.Text.Json;
using Ashok.Core.Contracts;
using Ashok.Core.Playbooks;
using Ashok.Core.Resolution;

namespace Ashok.Core.Tests;

/// <summary>Checks the real playbooks/*.yaml files: valid, complete, and pointing at things that exist on screen.</summary>
public class PlaybookFileTests
{
    private static readonly string[] ExpectedIds =
    [
        "bluetooth-on", "change-display-scaling", "change-volume", "connect-wifi", "edit-path",
        "find-downloads", "open-downloaded-file", "pair-device", "take-screenshot", "uninstall-app",
    ];

    private static readonly string PlaybooksDir = FindDirectory("playbooks");

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<UiElement>>> Screens = new(LoadScreens);

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var id in ExpectedIds) data.Add(id);
        return data;
    }

    private static Playbook Load(string id) => PlaybookLoader.Load(Path.Combine(PlaybooksDir, $"{id}.yaml"));

    [Fact]
    public void Exactly_the_ten_mvp_playbooks_exist_for_windows_11()
    {
        var store = PlaybookStore.LoadDirectory(PlaybooksDir, "win11");

        Assert.Equal(ExpectedIds, store.All.Select(p => p.Id));
        Assert.All(Directory.GetFiles(PlaybooksDir, "*.yaml"), f => Assert.Contains(Path.GetFileNameWithoutExtension(f), ExpectedIds));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Playbook_is_complete_and_gentle(string id)
    {
        var playbook = Load(id);

        Assert.Equal(id, playbook.Id);
        Assert.True(playbook.Examples.Count >= 4, "needs several example phrasings for intent matching");
        Assert.True(playbook.Steps.Count >= 2);
        Assert.Equal(Enumerable.Range(1, playbook.Steps.Count).Select(n => $"s{n}"), playbook.Steps.Select(s => s.Id));
        Assert.All(playbook.Steps, s =>
        {
            Assert.True(s.Say.Length is >= 10 and <= 240, $"{id}/{s.Id}: say is {s.Say.Length} characters");
            Assert.EndsWith(".", s.Say);
        });
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_target_resolves_on_a_recorded_screen(string id)
    {
        var resolver = new TargetResolver();
        var screens = Screens.Value;
        var missing = new List<string>();

        foreach (var step in Load(id).Steps)
        {
            foreach (var (label, target) in Targets(step))
            {
                // A doneWhen needs the exact role (it decides what is on screen); a step target may use a compatible one.
                var found = screens.Any(screen => (label == "doneWhen"
                    ? resolver.BestExactRole(target, screen.Value)
                    : resolver.Best(target, screen.Value)) is not null);
                if (!found) missing.Add($"{id}/{step.Id} {label}: {target.Role} '{target.Name}'");
            }
        }

        Assert.True(missing.Count == 0, "No recorded screen contains: " + string.Join("; ", missing));
    }

    [Fact]
    public void Intermediate_steps_are_not_satisfied_by_the_screen_before_them()
    {
        // The Bluetooth switch must not count as "visible" just because the Settings nav is.
        var resolver = new TargetResolver();
        var bluetooth = Load("bluetooth-on");
        var s2Done = bluetooth.Steps[1].DoneWhen.Target!;

        Assert.Null(resolver.BestExactRole(s2Done, Screens.Value["settings-nav"]));

        // Even on the Bluetooth page, a breadcrumb button named "Bluetooth & devices" is not the switch.
        Assert.Equal("Bluetooth", resolver.BestExactRole(s2Done, Screens.Value["bluetooth-page"])!.Element.Name);
    }

    [Fact]
    public void A_step_is_not_satisfied_by_the_screen_before_it_just_because_a_control_shares_its_name()
    {
        var resolver = new TargetResolver();

        // connect-wifi s3: the Wi-Fi switch is on the Wi-Fi page, not the Network page's quick toggle.
        var wifi = Load("connect-wifi").Steps[2].DoneWhen.Target!;
        Assert.Null(resolver.BestExactRole(wifi, Screens.Value["network-page"]));
        Assert.NotNull(resolver.BestExactRole(wifi, Screens.Value["wifi-page"]));

        // pair-device s3: the dialog's Bluetooth button is not the page's Bluetooth switch.
        var dialogButton = Load("pair-device").Steps[2].DoneWhen.Target!;
        Assert.Null(resolver.BestExactRole(dialogButton, Screens.Value["bluetooth-page"]));
        Assert.NotNull(resolver.BestExactRole(dialogButton, Screens.Value["add-device-dialog"]));
    }

    [Fact]
    public void The_downloads_sidebar_item_resolves_to_the_item_and_the_folder_tab_to_the_tab()
    {
        var resolver = new TargetResolver();
        var steps = Load("find-downloads").Steps;
        var explorer = Screens.Value["explorer-folder"];

        var sidebar = resolver.Best(steps[1].Target!, explorer)!;
        var tab = resolver.Best(steps[1].DoneWhen.Target!, explorer)!;

        Assert.Equal("TreeItem", sidebar.Element.Role);
        Assert.Equal("TabItem", tab.Element.Role);
    }

    [Fact]
    public void Downloads_is_not_done_on_This_PC_where_only_the_sidebar_item_exists()
    {
        var resolver = new TargetResolver();
        var done = Load("find-downloads").Steps[1].DoneWhen.Target!;
        var thisPc = new[] { Screens.Value["explorer-folder"][2] }; // only the sidebar TreeItem

        Assert.Null(resolver.BestExactRole(done, thisPc));
        Assert.NotNull(resolver.BestExactRole(done, Screens.Value["explorer-folder"]));
    }

    [Fact]
    public void The_PATH_row_is_found_in_the_user_list_first()
    {
        var resolver = new TargetResolver();
        var target = Load("edit-path").Steps[2].Target!;

        var best = resolver.Best(target, Screens.Value["environment-variables"]);

        Assert.Equal("ListViewItem-5", best!.Element.AutomationId);
    }

    private static IEnumerable<(string Label, Target Target)> Targets(PlanStep step)
    {
        if (step.Target is { } target) yield return ("target", target);
        if (step.DoneWhen.Target is { } done) yield return ("doneWhen", done);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<UiElement>> LoadScreens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "screens.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, IReadOnlyList<UiElement>>();
        var n = 0;

        foreach (var screen in doc.RootElement.GetProperty("screens").EnumerateObject())
        {
            var elements = new List<UiElement>();
            foreach (var e in screen.Value.EnumerateArray())
            {
                elements.Add(new UiElement(
                    $"e{++n}",
                    e.GetProperty("role").GetString()!,
                    e.GetProperty("name").GetString()!,
                    e.TryGetProperty("automationId", out var id) ? id.GetString() : null,
                    [0, elements.Count * 40, 100, 30],
                    true,
                    e.TryGetProperty("toggle", out var toggle) ? toggle.GetString() : null));
            }
            result[screen.Name] = elements;
        }
        return result;
    }

    private static string FindDirectory(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return candidate;
        }
        throw new DirectoryNotFoundException($"{name}/ not found above {AppContext.BaseDirectory}");
    }
}
