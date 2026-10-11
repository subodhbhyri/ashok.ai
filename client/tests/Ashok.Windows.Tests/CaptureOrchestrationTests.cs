using Ashok.Core.Capture;

namespace Ashok.Windows.Tests;

internal sealed class FixedForegroundProvider(ForegroundWindow? window) : IForegroundWindowProvider
{
    public ForegroundWindow? Get() => window;
}

internal sealed class RecordingSource(IReadOnlyList<RawElement>? elements = null) : IElementSource
{
    public int Reads { get; private set; }

    public IReadOnlyList<RawElement> Read(IntPtr windowHandle)
    {
        Reads++;
        return elements ?? [];
    }
}

public class CaptureOrchestrationTests
{
    private static readonly RawElement Button = new("Button", "OK", null, 0, 0, 10, 10, true, null, false, false);

    [Theory]
    [InlineData("KeePass")]
    [InlineData("1password")]
    [InlineData("Bitwarden")]
    public void Sensitive_process_windows_are_never_read(string process)
    {
        var source = new RecordingSource([Button]);
        var capture = new UiaCapture(
            new FixedForegroundProvider(new ForegroundWindow(new IntPtr(1), process, "Vault - secret title")),
            source, new DocumentOrderRanker());

        var snapshot = capture.Capture();
        var (_, candidates, _) = capture.CaptureCandidates();

        Assert.Equal(0, source.Reads);
        Assert.Empty(snapshot.Elements);
        Assert.Empty(candidates);
        Assert.Equal("", snapshot.Foreground.Title);
        Assert.Equal(process, snapshot.Foreground.Process);
    }

    [Fact]
    public void Custom_sensitive_list_is_honoured()
    {
        var source = new RecordingSource([Button]);
        var capture = new UiaCapture(
            new FixedForegroundProvider(new ForegroundWindow(new IntPtr(1), "mybank", "Accounts")),
            source, new DocumentOrderRanker(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mybank" });

        Assert.Empty(capture.Capture().Elements);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void Other_processes_are_read_and_redacted()
    {
        var password = Button with { Name = "Secret", IsPassword = true };
        var source = new RecordingSource([Button, password]);
        var capture = new UiaCapture(
            new FixedForegroundProvider(new ForegroundWindow(new IntPtr(1), "explorer", "Downloads")),
            source, new DocumentOrderRanker());

        var snapshot = capture.Capture();

        Assert.Equal(1, source.Reads);
        Assert.Single(snapshot.Elements);
        Assert.Equal(1, snapshot.Redacted);
    }

    [Fact]
    public void No_foreground_window_gives_an_empty_snapshot()
    {
        var source = new RecordingSource([Button]);
        var snapshot = new UiaCapture(new FixedForegroundProvider(null), source, new DocumentOrderRanker()).Capture();

        Assert.Empty(snapshot.Elements);
        Assert.Equal(0, source.Reads);
    }
}
