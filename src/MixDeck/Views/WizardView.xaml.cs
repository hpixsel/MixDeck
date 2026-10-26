using System.Windows.Controls;

namespace MixDeck.Views;

public partial class WizardView : UserControl
{
    public WizardView()
    {
        InitializeComponent();
        Logo.Source = AppIcon.CreateImage(96);
    }
}
