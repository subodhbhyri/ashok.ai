using Ashok.Core.Capture;
using Ashok.Core.Contracts;

namespace Ashok.Windows;

/// <summary>Captures the foreground window as a ui-snapshot.</summary>
public sealed class UiaCapture(
    IForegroundWindowProvider foreground,
    IElementSource source,
    IElementRanker ranker,
    IReadOnlySet<string>? sensitiveProcesses = null)
{
    public const int MaxElements = 150;

    public UiSnapshot Capture()
    {
        var (window, candidates, redacted) = CaptureCandidates();
        var ranked = ranker.Rank(candidates, MaxElements);
        return new UiSnapshot(
            OsInfoProvider.Get(),
            new ForegroundInfo(window?.Process ?? "none", window?.Title ?? ""),
            ranked.Elements, redacted, ranked.Truncated);
    }

    /// <summary>Every candidate after redaction and before ranking (no cap). Used by the dev tool's --raw.</summary>
    public (ForegroundWindow? Window, IReadOnlyList<UiElement> Candidates, int Redacted) CaptureCandidates()
    {
        var window = foreground.Get();
        if (window is null) return (null, [], 0);

        // Checked before any UIA call, so these windows are never walked or read.
        if (Redaction.IsSensitiveProcess(window.Process, sensitiveProcesses))
            return (window with { Title = "" }, [], 0);

        var redaction = Redaction.Apply(source.Read(window.Handle));
        return (window, redaction.Candidates, redaction.Redacted);
    }
}
