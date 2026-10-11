using Ashok.Core.Contracts;

namespace Ashok.Core.Capture;

public sealed record RedactionResult(IReadOnlyList<UiElement> Candidates, int Redacted);

/// <summary>Privacy and size rules applied to raw elements before ranking.</summary>
public static class Redaction
{
    public const int MaxNameLength = 80;
    public const int MaxTextNameLength = 200;

    /// <summary>Windows from these processes are never read at all.</summary>
    public static readonly IReadOnlySet<string> SensitiveProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass", "Dashlane", "NordPass", "Enpass", "RoboForm",
    };

    /// <summary>Case-insensitive; accepts the process name with or without ".exe".</summary>
    public static bool IsSensitiveProcess(string processName, IReadOnlySet<string>? list = null)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return (list ?? SensitiveProcesses).Contains(name);
    }

    /// <summary>
    /// Drops passwords (counted in Redacted), offscreen and zero-size elements, and overlong Text;
    /// truncates names. Refs are assigned here, in input order, so raw and ranked output share them.
    /// </summary>
    public static RedactionResult Apply(IEnumerable<RawElement> elements)
    {
        var candidates = new List<UiElement>();
        var redacted = 0;

        foreach (var e in elements)
        {
            if (e.IsPassword) { redacted++; continue; }
            if (e.IsOffscreen || e.Width <= 0 || e.Height <= 0) continue;

            var name = e.Name ?? "";
            if (e.Role == "Text" && name.Length > MaxTextNameLength) continue;

            candidates.Add(new UiElement(
                $"e{candidates.Count + 1}",
                e.Role,
                Truncate(name, MaxNameLength),
                string.IsNullOrEmpty(e.AutomationId) ? null : e.AutomationId,
                [e.X, e.Y, e.Width, e.Height],
                e.Enabled,
                e.Toggle));
        }

        return new RedactionResult(candidates, redacted);
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var end = char.IsLowSurrogate(s[max]) ? max - 1 : max; // don't split a surrogate pair
        return s[..end];
    }
}
