using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ashok.App.Tray;

/// <summary>The notification-area icon and its menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _ringIcon;

    public TrayIcon()
    {
        _ringIcon = DrawRingIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ask…", null, (_, _) => AskRequested?.Invoke());
        menu.Items.Add("Demo ring", null, (_, _) => DemoRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = _ringIcon,
            Text = "Ashok — press Ctrl+Alt+H to ask",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) AskRequested?.Invoke();
        };
    }

    public event Action? AskRequested;
    public event Action? DemoRequested;
    public event Action? QuitRequested;

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);

    // Drawn in code so no binary icon asset has to be committed.
    private static Icon DrawRingIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(0xFF, 0x00, 0xAA), 5);
            g.DrawEllipse(pen, 4, 4, 23, 23);
        }
        // GetHicon leaks the native handle; clone then free it.
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ringIcon.Dispose();
    }
}
