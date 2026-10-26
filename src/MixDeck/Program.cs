using Velopack;

namespace MixDeck;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: handles install/update/uninstall hooks of the Velopack installer and exits when it does
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Set(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
