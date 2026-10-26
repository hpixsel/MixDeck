using System.Drawing;
using System.Windows.Forms;
using System.Windows.Media.Imaging;

namespace MixDeck;

/// <summary>Notification-area icon: click for the compact mixer, double-click or menu for the full window.</summary>
sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;

    /// <param name="toggleFlyout">Left click: the compact mixer.</param>
    /// <param name="show">Menu / double click: the full window.</param>
    public TrayIcon(Action toggleFlyout, Action show, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open MixDeck", null, (_, _) => show());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(32),
            Text = "MixDeck",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggleFlyout(); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>The MixDeck logo (assets\MixDeck.ico and logo.png, drawn by build\make-icon.ps1).</summary>
static class AppIcon
{
    static Stream Resource(string name) =>
        System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}")).Stream;

    /// <summary>Icon for the notification area, picked from the .ico at the requested size.</summary>
    public static Icon Create(int size)
    {
        using var s = Resource("MixDeck.ico");
        return new Icon(s, size, size);
    }

    public static BitmapSource CreateImage(int size)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = Resource("logo.png");
        img.DecodePixelWidth = size;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
