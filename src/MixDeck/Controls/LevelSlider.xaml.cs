using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MixDeck.Controls;

/// <summary>
/// Volume slider that also shows the live signal: the translucent fill is the volume setting,
/// the solid bar inside it is what is actually playing. Wheel adjusts, double-click resets to 100%.
/// </summary>
public partial class LevelSlider : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((LevelSlider)d).UpdateBars()));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelSlider), new PropertyMetadata(0.0, (d, _) => ((LevelSlider)d).UpdateBars()));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted), typeof(bool), typeof(LevelSlider), new PropertyMetadata(false, (d, _) => ((LevelSlider)d).UpdateBars()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(LevelSlider), new PropertyMetadata(Brushes.White));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public LevelSlider()
    {
        InitializeComponent();
        Host.SizeChanged += (_, _) => UpdateBars();
        PreviewMouseWheel += OnWheel;
        MouseDoubleClick += (_, e) =>
        {
            Value = 100;
            e.Handled = true;
        };
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Value = Math.Clamp(Math.Round(Value) + (e.Delta > 0 ? 2 : -2), 0, 100);
        e.Handled = true;
    }

    void UpdateBars()
    {
        double w = Track.ActualWidth;
        double fill = Math.Clamp(Value / 100, 0, 1);
        Fill.Width = w * fill;
        Meter.Width = IsMuted ? 0 : w * Math.Clamp(Level, 0, 1);
        Opacity = IsMuted ? 0.4 : 1;
    }
}
