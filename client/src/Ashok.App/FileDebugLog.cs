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

    public void Verdict(DateTimeOffset at, OffPlanVerdict verdict, string? reason, string process, string stepId, double secondsIntoStep, string action = "replan") =>
        Append(string.Create(CultureInfo.InvariantCulture,
            $"{at:O} verdict={verdict} reason={reason ?? "-"} process={process} step={stepId} secs={secondsIntoStep:F1} action={action}"));

    public void AutomationReset(DateTimeOffset at, string process, int hresult) =>
        Append(string.Create(CultureInfo.InvariantCulture, $"{at:O} automation-reset process={process} hresult=0x{hresult:X8}"));

    public void Routed(DateTimeOffset at, string? process, string? playbookId, double? score, string path)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{at:O} process={process ?? "-"} playbook={playbookId ?? "-"} score={(score is { } s ? s.ToString("F3", CultureInfo.InvariantCulture) : "-")} path={path}");
        Append(line);
    }

    private void Append(string line)
    {
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
