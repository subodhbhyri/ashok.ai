using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Ashok.Speech;
using Ashok.UI;
using Ashok.UI.Hotkey;
using Xunit.Abstractions;

namespace Ashok.Windows.Tests;

/// <summary>Windows that need a real desktop session. Local only.</summary>
[Trait("Category", "Desktop")]
public class UiComponentTests(ITestOutputHelper output)
{
    [Fact]
    public void Hotkey_raises_Pressed_when_WM_HOTKEY_arrives()
    {
        using var sta = new StaThread();
        var hotkey = sta.Invoke(() => new GlobalHotkey());
        try
        {
            output.WriteLine($"Ctrl+Alt+H registered: {hotkey.IsRegistered}" +
                             (hotkey.IsRegistered ? "" : " (another program, possibly a running Ashok, owns it)"));
            using var pressed = new ManualResetEventSlim();
            sta.Invoke(() => hotkey.Pressed += pressed.Set);

            // No real keystroke: post the message the system would send.
            TestNative.PostMessage(hotkey.WindowHandle, GlobalHotkey.WmHotkey, new IntPtr(GlobalHotkey.HotkeyId), IntPtr.Zero);

            Assert.True(pressed.Wait(TimeSpan.FromSeconds(5)), "Pressed was not raised.");
        }
        finally
        {
            sta.Invoke(hotkey.Dispose);
        }
    }

    [Fact]
    public void Hotkey_can_be_registered_again_after_disposal()
    {
        using var sta = new StaThread();
        var first = sta.Invoke(() => new GlobalHotkey());
        var wasFree = first.IsRegistered;
        sta.Invoke(first.Dispose);
        if (!wasFree)
        {
            output.WriteLine("Ctrl+Alt+H is held by another program; cannot check release.");
            return;
        }

        var second = sta.Invoke(() => new GlobalHotkey());
        try { Assert.True(second.IsRegistered, "Hotkey was not released by Dispose."); }
        finally { sta.Invoke(second.Dispose); }
    }

    private static Button AskButton(AskPanel panel) => (Button)LogicalTreeHelper.FindLogicalNode(panel, "AskButton");

    private static TextBox QuestionBox(AskPanel panel) => (TextBox)LogicalTreeHelper.FindLogicalNode(panel, "QuestionBox");

    // Synchronous, unlike the automation peer's Invoke, which queues the click on the dispatcher.
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void AskPanel_raises_the_trimmed_question_and_previous_window()
    {
        using var sta = new StaThread();
        var panel = sta.Invoke(() => new AskPanel());
        var received = new List<(string Text, IntPtr Window)>();
        sta.Invoke(() => panel.QuestionSubmitted += (t, w) => received.Add((t, w)));
        try
        {
            sta.Invoke(() =>
            {
                panel.ShowForQuestion(new IntPtr(1234));
                QuestionBox(panel).Text = "  how do I turn on bluetooth  ";
                Click(AskButton(panel));
            });

            var (text, window) = Assert.Single(received);
            Assert.Equal("how do I turn on bluetooth", text);
            Assert.Equal(new IntPtr(1234), window);
        }
        finally
        {
            sta.Invoke(panel.Hide);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AskPanel_ignores_empty_questions(string input)
    {
        using var sta = new StaThread();
        var panel = sta.Invoke(() => new AskPanel());
        var raised = false;
        sta.Invoke(() => panel.QuestionSubmitted += (_, _) => raised = true);
        try
        {
            sta.Invoke(() =>
            {
                panel.ShowForQuestion(IntPtr.Zero);
                QuestionBox(panel).Text = input;
                Click(AskButton(panel));
            });

            Assert.False(raised);
        }
        finally
        {
            sta.Invoke(panel.Hide);
        }
    }

    [Fact]
    public void AskPanel_is_topmost_uses_large_text_and_hides_on_Escape()
    {
        using var sta = new StaThread();
        var panel = sta.Invoke(() => new AskPanel());
        try
        {
            var (topmost, fontSize, visibleAfterShow) = sta.Invoke(() =>
            {
                panel.ShowForQuestion(IntPtr.Zero);
                return (panel.Topmost, QuestionBox(panel).FontSize, panel.IsVisible);
            });
            Assert.True(visibleAfterShow);
            Assert.True(topmost);
            Assert.True(fontSize >= 24, $"Question text is {fontSize} pt.");

            var visibleAfterEscape = sta.Invoke(() =>
            {
                var source = PresentationSource.FromVisual(panel)!;
                panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                });
                return panel.IsVisible;
            });
            Assert.False(visibleAfterEscape);
        }
        finally
        {
            sta.Invoke(panel.Hide);
        }
    }

    [Fact]
    public void WindowsSpeaker_synthesizes_audio()
    {
        using var speaker = new WindowsSpeaker();
        if (!speaker.HasVoice)
        {
            output.WriteLine("No speech voice is installed; synthesis not checked.");
            return;
        }

        var wave = speaker.RenderToWave("Click Bluetooth and devices on the left.");

        output.WriteLine($"Rendered {wave.Length} bytes.");
        Assert.True(wave.Length > 10_000);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
    }
}
