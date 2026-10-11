using System.Diagnostics;
using Ashok.Core.Capture;
using Ashok.Core.Contracts;
using Ashok.Windows;

const string Usage = "Usage: Ashok.DevTool snapshot [--delay <seconds>] [--out <dir>] [--raw]";

if (args.Length == 0 || args[0] != "snapshot")
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var delay = 3;
string? outDir = null;
var raw = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--delay" when i + 1 < args.Length && int.TryParse(args[++i], out var d) && d >= 0: delay = d; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--raw": raw = true; break;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

outDir ??= Path.Combine(FindRepoRoot() ?? Directory.GetCurrentDirectory(), "eval", "snapshots");
Directory.CreateDirectory(outDir);

for (var s = delay; s > 0; s--)
{
    Console.WriteLine($"Capturing in {s}... (focus the window you want)");
    Thread.Sleep(1000);
}

using var source = new FlaUiElementSource();
var capture = new UiaCapture(new Win32ForegroundWindowProvider(), source, new DocumentOrderRanker());

var sw = Stopwatch.StartNew();
var snapshot = capture.Capture();
var coldMs = sw.ElapsedMilliseconds;
sw.Restart();
capture.Capture();
var warmMs = sw.ElapsedMilliseconds;

var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var path = Path.Combine(outDir, $"{snapshot.Foreground.Process}-{stamp}.json");
File.WriteAllText(path, SnapshotJson.Serialize(snapshot));
Console.WriteLine($"{snapshot.Foreground.Process}: {snapshot.Elements.Count} elements" +
                  $"{(snapshot.Truncated ? " (truncated)" : "")}, {snapshot.Redacted} redacted, " +
                  $"{coldMs} ms cold / {warmMs} ms warm");
Console.WriteLine(path);

if (raw)
{
    var (window, candidates, redacted) = capture.CaptureCandidates();
    var rawPath = Path.ChangeExtension(path, ".raw.json");
    File.WriteAllText(rawPath, SnapshotJson.Serialize(new
    {
        foreground = new ForegroundInfo(window?.Process ?? "none", window?.Title ?? ""),
        redacted,
        candidateCount = candidates.Count,
        candidates,
    }));
    Console.WriteLine($"{candidates.Count} candidates before ranking -> {rawPath}");
}

return 0;

static string? FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
    return null;
}
