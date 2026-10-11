using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Ashok.UI;

/// <summary>A small, always-on-top window with large text where the user types a question.</summary>
public partial class AskPanel : Window
{
    private IntPtr _previousWindow;

    public AskPanel() => InitializeComponent();

    /// <summary>
    /// Raised with the trimmed question and the window that was in the foreground before the panel
    /// opened, which is the window the user needs help with.
    /// </summary>
    public event Action<string, IntPtr>? QuestionSubmitted;

    /// <param name="previousWindow">Foreground window captured before this panel takes focus.</param>
    public void ShowForQuestion(IntPtr previousWindow)
    {
        _previousWindow = previousWindow;
        QuestionBox.Clear();
        AnswerText.Visibility = Visibility.Collapsed;
        Show();
        Activate();
        QuestionBox.Focus();
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
    }

    private void OnAskClick(object sender, RoutedEventArgs e) => Submit();

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
}
