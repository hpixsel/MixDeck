using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using MixDeck.ViewModels;

namespace MixDeck.Views;

/// <summary>Dialogs for microphone effects and output EQ/limiter; closed by ✕, Esc or a click outside.</summary>
public partial class FxEditors : UserControl
{
    public FxEditors()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    MainViewModel Vm => (MainViewModel)DataContext;

    void Close()
    {
        Vm.InputFxEditor = null;
        Vm.OutputFxEditor = null;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Close();
    void Dialog_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }
}
