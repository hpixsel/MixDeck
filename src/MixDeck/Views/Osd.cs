using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MixDeck.Views;

/// <summary>Small on-screen confirmation for hotkeys ("Music · muted", "Chat · 60%"). Never takes focus.</summary>
public sealed class Osd : Window
{
    readonly TextBlock _glyph = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _detail = new() { FontSize = 12 };
    readonly Border _bar = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _barTrack = new() { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0) };
    readonly DispatcherTimer _hide;

    public Osd()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;

        _glyph.FontFamily = (FontFamily)Application.Current.FindResource("IconFont");
        _title.Foreground = (Brush)Application.Current.FindResource("TextBrush");
        _detail.Foreground = (Brush)Application.Current.FindResource("SubtleText");
        _barTrack.Background = (Brush)Application.Current.FindResource("TrackBg");
        _barTrack.Child = _bar;

        var text = new StackPanel { MinWidth = 200 };
        text.Children.Add(_title);
        text.Children.Add(_detail);
        text.Children.Add(_barTrack);
        var row = new DockPanel();
        row.Children.Add(_glyph);
        row.Children.Add(text);
        Content = new Border
        {
            Background = (Brush)Application.Current.FindResource("Surface2Bg"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12, 20, 12),
            Child = row,
        };

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => Hide();
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <param name="level">0–100 to show a bar, or null for none.</param>
    public void Show(string glyph, Brush accent, string title, string detail, double? level = null)
    {
        _glyph.Text = glyph;
        _glyph.Foreground = accent;
        _title.Text = title;
        _detail.Text = detail;
        _bar.Background = accent;
        _barTrack.Visibility = level == null ? Visibility.Collapsed : Visibility.Visible;
        _bar.Width = 200 * Math.Clamp(level ?? 0, 0, 100) / 100;

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) base.Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 80;
        _hide.Stop();
        _hide.Start();
    }
}
