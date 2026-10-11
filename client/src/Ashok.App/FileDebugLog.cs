using System.Globalization;
using System.IO;
using Ashok.Core.Session;

namespace Ashok.App;

/// <summary>
/// One line per question in %LOCALAPPDATA%\Ashok\debug.log: time, process of the captured window, matched playbook and score,
/// and which path was taken. No question text and no screen contents. Never throws.
/// </summary>
public sealed class FileDebugLog(string? path = null) : IDebugLog
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ashok", "debug.log");

    private readonly object _gate = new();

    public void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{at:O} process={process ?? "-"} playbook={playbookId ?? "-"} score={(score is { } s ? s.ToString("F3", CultureInfo.InvariantCulture) : "-")} path={path}");
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A diagnostics file must never get in the way of answering the question.
        }
    }
}
