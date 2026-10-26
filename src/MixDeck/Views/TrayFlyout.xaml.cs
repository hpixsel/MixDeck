using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MixDeck.ViewModels;

namespace MixDeck.Views;

/// <summary>Compact mixer that pops up above the tray icon; closes when it loses focus.</summary>
public partial class TrayFlyout : Window
{
    readonly Action _openMain;
    DateTime _hiddenAt;

    MainViewModel Vm => (MainViewModel)DataContext;

    public TrayFlyout(Action openMain)
    {
        _openMain = openMain;
        InitializeComponent();
        Logo.Source = AppIcon.CreateImage(32);
        Deactivated += (_, _) =>
        {
            _hiddenAt = DateTime.UtcNow;
            Hide();
        };
    }

    public void Toggle()
    {
        // A click on the tray icon first deactivates (hides) us; don't immediately reopen on that same click
        if (IsVisible || (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300)
        {
            Hide();
            return;
        }
        Vm.RefreshFlyout();
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();
    }

    void Mix_Loaded(object sender, RoutedEventArgs e)
    {
        var rb = (RadioButton)sender;
        rb.IsChecked = rb.DataContext == Vm.FlyoutMix;
    }

    void Mix_Checked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is MixViewModel mix) Vm.FlyoutMix = mix;
    }

    void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 1 && e.AddedItems[0] is ProfileViewModel p && p != Vm.ActiveProfile)
            Vm.ApplyProfile(p, automatic: false);
    }

    void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _openMain();
    }
}
