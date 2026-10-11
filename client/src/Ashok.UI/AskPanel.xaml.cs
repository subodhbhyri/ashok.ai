using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Ashok.Core.Session;

namespace Ashok.UI;

/// <summary>
/// A small, always-on-top window with large text. The user types a question here; after that it becomes a
/// quiet step card that stays visible but never holds focus, so "press Windows + E" or typing into a field
/// goes to the user's own window and not to Ashok.
/// </summary>
public partial class AskPanel : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    private IntPtr _previousWindow;

    public AskPanel() => InitializeComponent();

    /// <summary>
    /// Raised with the trimmed question and the window that was in the foreground before the panel
    /// opened, which is the window the user needs help with.
    /// </summary>
    public event Action<string, IntPtr>? QuestionSubmitted;

    /// <summary>The user pressed Done on a step that waits for them to say so.</summary>
    public event Action? ConfirmRequested;

    /// <summary>The user pressed Stop.</summary>
    public event Action? StopRequested;

    /// <summary>Whether the last attempt to give focus back to the user's window worked. For diagnostics and tests.</summary>
    public bool FocusReturned { get; private set; }

    /// <param name="previousWindow">Foreground window captured before this panel takes focus.</param>
    public void ShowForQuestion(IntPtr previousWindow)
    {
        _previousWindow = previousWindow;
        SetActivatable(true);
        GuideArea.Visibility = Visibility.Collapsed;
        AskArea.Visibility = Visibility.Visible;
        QuestionBox.Clear();
        AnswerText.Visibility = Visibility.Collapsed;
        Show();
        Activate();
        QuestionBox.Focus();
    }

    /// <summary>Shows one step. Safe to call repeatedly; the panel stays inactive.</summary>
    public void ShowStep(StepView step)
    {
        EnterGuideLayout();
        StepCounter.Text = $"Step {step.Index + 1} of {step.Total}";
        StepText.Text = step.Say;
        DoneButton.Visibility = step.NeedsConfirm ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows a closing or explanatory message in place of a step.</summary>
    public void ShowMessage(string text)
    {
        EnterGuideLayout();
        StepCounter.Text = "";
        StepText.Text = text;
        DoneButton.Visibility = Visibility.Collapsed;
    }

    public void ClearGuidance()
    {
        StepCounter.Text = "";
        StepText.Text = "";
        DoneButton.Visibility = Visibility.Collapsed;
    }

    public void SetAnswer(string text)
    {
        AnswerText.Text = text;
        AnswerText.Visibility = Visibility.Visible;
    }

    // Closing the panel only hides it, so the tray app keeps one instance for its whole life.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Submit()
    {
        var text = QuestionBox.Text.Trim();
        if (text.Length == 0) return;

        QuestionSubmitted?.Invoke(text, _previousWindow);

        EnterGuideLayout();
        StepCounter.Text = "";
        StepText.Text = "Working on it…";
        HandFocusBack();
    }

    private void EnterGuideLayout()
    {
        AskArea.Visibility = Visibility.Collapsed;
        GuideArea.Visibility = Visibility.Visible;
        if (IsVisible) SetActivatable(false);
    }

    // Stop the panel from taking focus when clicked (its buttons still work), then give the user's window focus back.
    private void HandFocusBack()
    {
        SetActivatable(false);
        FocusReturned = _previousWindow != IntPtr.Zero && _previousWindow != new WindowInteropHelper(this).Handle &&
                        IsWindow(_previousWindow) && SetForegroundWindow(_previousWindow);
    }

    private void SetActivatable(bool activatable)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, activatable ? style & ~WsExNoActivate : style | WsExNoActivate);
    }

    private void OnAskClick(object sender, RoutedEventArgs e) => Submit();

    private void OnDoneClick(object sender, RoutedEventArgs e) => ConfirmRequested?.Invoke();

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke();
        Hide();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Hide();
        e.Handled = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
}

/// <summary>Adapts the panel to the session's view interface.</summary>
public sealed class PanelSessionView(AskPanel panel) : ISessionView
{
    public void ShowStep(StepView step) => panel.ShowStep(step);

    public void ShowMessage(string text) => panel.ShowMessage(text);

    public void Clear() => panel.ClearGuidance();
}
