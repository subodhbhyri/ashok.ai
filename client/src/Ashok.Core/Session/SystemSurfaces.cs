namespace Ashok.Core.Session;

/// <summary>Which foreground processes are Windows itself (system playbooks apply there) rather than a regular app.</summary>
public static class SystemSurfaces
{
    // explorer covers File Explorer, the desktop and the taskbar; "none" means nothing is in front.
    private static readonly HashSet<string> Processes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SystemSettings", "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "control", "none", "unknown",
    };

    public static bool IsRegularApp(string process)
    {
        var name = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        return name.Length > 0 && !Processes.Contains(name);
    }
}
