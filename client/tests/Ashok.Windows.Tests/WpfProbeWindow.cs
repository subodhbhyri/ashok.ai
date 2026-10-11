using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Ashok.Windows.Tests;

/// <summary>A window we fully control: an edit with a value, a password box and a fixed-size button.</summary>
public sealed class WpfProbeWindow : IDisposable
{
    public const string EditValue = "SENTINEL-EDIT-VALUE-7f3a";
    public const string PasswordValue = "SENTINEL-PASSWORD-91cd";
    public const double ButtonWidthDip = 120;
    public const double ButtonHeightDip = 40;

    private readonly Thread _thread;
    private Dispatcher _dispatcher = null!;
    private Window _window = null!;
    private Button _button = null!;
    private readonly ManualResetEventSlim _ready = new();

    public IntPtr Handle { get; private set; }

    public WpfProbeWindow()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WpfProbeWindow" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Probe window did not open.");
    }

    /// <summary>Button width in physical pixels computed by WPF at the window's DPI.</summary>
    public (double Width, double Height, double DpiScale) ButtonPhysicalSize() => _dispatcher.Invoke(() =>
    {
        var scale = VisualTreeHelper_GetDpiScale(_window);
        return (_button.ActualWidth * scale, _button.ActualHeight * scale, scale);
    });

    private static double VisualTreeHelper_GetDpiScale(Window w) => System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;

    private void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        var edit = new TextBox { Text = EditValue, Margin = new Thickness(8) };
        AutomationProperties.SetName(edit, "Email");
        var password = new PasswordBox { Password = PasswordValue, Margin = new Thickness(8) };
        AutomationProperties.SetName(password, "Secret");
        _button = new Button { Content = "Probe", Width = ButtonWidthDip, Height = ButtonHeightDip, Margin = new Thickness(8) };

        var panel = new StackPanel();
        panel.Children.Add(edit);
        panel.Children.Add(password);
        panel.Children.Add(_button);

        _window = new Window
        {
            Title = "Ashok probe window",
            Width = 400,
            Height = 300,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        _window.Show();
        Handle = new WindowInteropHelper(_window).Handle;
        _dispatcher.InvokeAsync(() => _ready.Set(), DispatcherPriority.ApplicationIdle);

        Dispatcher.Run();
    }

    public void Dispose()
    {
        if (_dispatcher is null) return;
        _dispatcher.Invoke(() => _window.Close());
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
