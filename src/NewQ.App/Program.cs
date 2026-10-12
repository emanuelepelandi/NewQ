using System;
using Velopack;

namespace NewQ.App;

/// <summary>Entry point: Velopack first (install, update and uninstall hooks), then the WPF application.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // When Windows runs NewQ for an install/update/uninstall step, Velopack does it and exits here.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
