using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MixDeck.ViewModels;

namespace MixDeck.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox { IsReadOnly: true }) Close();
        };
        // Opened from a specific place (update badge, "Fix", channel menu): jump to the matching section
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || DataContext is not MainViewModel vm || vm.SettingsSection == null) return;
            FrameworkElement? target = vm.SettingsSection switch
            {
                "routing" => RoutingSection,
                "ducking" => DuckingSection,
                "updates" => UpdatesSection,
                _ => null,
            };
            vm.SettingsSection = null;
            if (target != null) Dispatcher.BeginInvoke(() => target.BringIntoView(new Rect(0, 0, 1, 400)), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    MainViewModel Vm => (MainViewModel)DataContext;

    void Close() => Vm.SettingsOpen = false;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Close();
    void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Records a hotkey: the next key combination typed into the box becomes the gesture.</summary>
    void Gesture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is not HotkeyViewModel h) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete) { h.Gesture = ""; return; }
        if (key == Key.Escape || key == Key.Tab) { Keyboard.ClearFocus(); return; }
        var gesture = HotkeyManager.GestureFrom(e);
        if (gesture == null) return;
        h.Gesture = gesture;
        Keyboard.ClearFocus();
    }

    void Gesture_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is HotkeyViewModel h) h.IsRecording = true;
        box.Text = "Press a shortcut…";
    }

    void Gesture_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is not HotkeyViewModel h) return;
        h.IsRecording = false;
        box.Text = h.Gesture;
    }
}
