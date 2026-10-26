using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MixDeck.ViewModels;

namespace MixDeck;

public partial class MainWindow : Window
{
    readonly App _app;
    Point _dragStart;
    AppViewModel? _dragCandidate;

    MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Icon = AppIcon.CreateImage(64);
        Logo.Source = AppIcon.CreateImage(64);
        SourceInitialized += (_, _) => TintTitleBar();
    }

    /// <summary>Windows 11: paint the title bar in the window background colour so the chrome blends in.</summary>
    void TintTitleBar()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x0014100E /* #0E1014 as COLORREF */, text = 0x00F0EAE8;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnClosing(CancelEventArgs e)
    {
        // The close button hides to the tray; audio keeps running. Exit from the tray menu.
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    #region Drag & drop of app chips between channels

    void Chip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as AppViewModel;
    }

    void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var app = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AppViewModel), app), DragDropEffects.Move);
        foreach (var ch in Vm.Channels) ch.IsDropTarget = false;
    }

    void Chip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Released without dragging: it was a click, so show this app's own volume
        if (_dragCandidate == null || (sender as FrameworkElement)?.DataContext is not AppViewModel app) return;
        _dragCandidate = null;
        AppVolumePopup.DataContext = app;
        AppVolumePopup.PlacementTarget = (UIElement)sender;
        AppVolumePopup.IsOpen = true;
        e.Handled = true;
    }

    void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 1 && e.AddedItems[0] is ProfileViewModel p && p != Vm.ActiveProfile)
            Vm.ApplyProfile(p, automatic: false);
    }

    static ChannelViewModel? DropTarget(object sender, DragEventArgs e)
    {
        var ch = (sender as FrameworkElement)?.DataContext as ChannelViewModel;
        var app = e.Data.GetData(typeof(AppViewModel)) as AppViewModel;
        return ch != null && app != null && ch.IsAppChannel && app.ChannelId != ch.Id ? ch : null;
    }

    void Channel_DragOver(object sender, DragEventArgs e)
    {
        var ch = DropTarget(sender, e);
        e.Effects = ch != null ? DragDropEffects.Move : DragDropEffects.None;
        if (ch != null) ch.IsDropTarget = true;
        e.Handled = true;
    }

    void Channel_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChannelViewModel ch) ch.IsDropTarget = false;
    }

    void Channel_Drop(object sender, DragEventArgs e)
    {
        var ch = DropTarget(sender, e);
        if (ch != null && e.Data.GetData(typeof(AppViewModel)) is AppViewModel app) Vm.MoveApp(app, ch);
        foreach (var c in Vm.Channels) c.IsDropTarget = false;
        e.Handled = true;
    }

    #endregion

    #region Menus

    /// <summary>Right-click on a chip: "Move to" each other app channel.</summary>
    void Chip_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var chip = (FrameworkElement)sender;
        if (chip.DataContext is not AppViewModel app || chip.ContextMenu == null) return;
        var menu = chip.ContextMenu;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = app.DisplayName, IsEnabled = false });
        menu.Items.Add(new Separator());
        foreach (var ch in Vm.Channels.Where(c => c.IsAppChannel))
        {
            var item = new MenuItem
            {
                Header = $"Move to: {ch.Name}",
                IsEnabled = ch.Id != app.ChannelId,
                Icon = new TextBlock { Text = ch.Glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"), Foreground = ch.Accent },
            };
            item.Click += (_, _) => Vm.MoveApp(app, ch);
            menu.Items.Add(item);
        }
    }

    /// <summary>Output menu: fallback devices (checkable, used in order) and delete.</summary>
    void MixMenu_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not MixViewModel mix) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Fallback devices (used when the main one is unplugged)", IsEnabled = false });
        var fallbacks = mix.Fallbacks;
        foreach (var dev in Vm.OutputDevices.Where(d => d.Id != null && d.Id != mix.OutputDeviceId))
        {
            int order = fallbacks.ToList().IndexOf(dev.Id!);
            var item = new MenuItem
            {
                Header = order >= 0 ? $"{order + 1}. {dev.Name}" : dev.Name,
                IsCheckable = true,
                IsChecked = order >= 0,
                StaysOpenOnClick = false,
            };
            item.Click += (_, _) => mix.ToggleFallback(dev.Id!);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var fx = new MenuItem { Header = "EQ and hearing protection…" };
        fx.Click += (_, _) => Vm.OpenOutputFx(mix);
        menu.Items.Add(fx);
        var delete = new MenuItem { Header = "Remove output", IsEnabled = Vm.Mixes.Count > 1 };
        delete.Click += (_, _) => mix.DeleteCommand.Execute(null);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    void ChannelMenu_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not ChannelViewModel ch) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        if (ch.IsInput)
        {
            var fx = new MenuItem { Header = "Microphone effects…" };
            fx.Click += (_, _) => Vm.OpenInputFx(ch);
            menu.Items.Add(fx);
        }
        var duck = new MenuItem { Header = "Auto-ducking…" };
        duck.Click += (_, _) => Vm.OpenSettings("ducking");
        menu.Items.Add(duck);
        if (ch.CanDelete)
        {
            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "Remove channel" };
            delete.Click += (_, _) => ch.DeleteCommand.Execute(null);
            menu.Items.Add(delete);
        }
        menu.IsOpen = true;
    }

    void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu == null) return;
        button.ContextMenu.DataContext = button.DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    void AddChannelButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddChannelButton, Placement = PlacementMode.Bottom };
        var apps = new MenuItem { Header = "App channel" };
        foreach (var (icon, label) in new[] { ("app", "General"), ("browser", "Browser"), ("music", "Music"), ("chat", "Chat"), ("game", "Games") })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Vm.AddAppChannel(icon);
            apps.Items.Add(item);
        }
        menu.Items.Add(apps);

        var inputs = new MenuItem { Header = "Microphone or other input" };
        foreach (var dev in Vm.InputDevices)
        {
            var item = new MenuItem { Header = dev.Name };
            item.Click += (_, _) => Vm.AddInputChannel(dev);
            inputs.Items.Add(item);
        }
        if (inputs.Items.Count == 0) inputs.Items.Add(new MenuItem { Header = "No recording devices", IsEnabled = false });
        menu.Items.Add(inputs);
        menu.IsOpen = true;
    }

    #endregion

    void UpdateBadge_Click(object sender, RoutedEventArgs e) => Vm.OpenSettings("updates");

    void InlineEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            // Moving focus away commits the binding (UpdateSourceTrigger=LostFocus)
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
        }
    }
}
