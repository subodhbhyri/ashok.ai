using Ashok.Core.Contracts;
using Microsoft.Win32;

namespace Ashok.Windows;

public static class OsInfoProvider
{
    public static OsInfo Get()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
        var edition = int.TryParse(build, out var n) && n >= 22000 ? "Windows 11" : "Windows 10";
        return new OsInfo(edition, build);
    }
}
