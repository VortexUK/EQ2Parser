using Velopack;

namespace EQ2Parser.App;

/// <summary>
/// Explicit entry point so the Velopack bootstrap runs before any WPF code —
/// it handles install/update/uninstall hooks and must be first in Main
/// (see https://docs.velopack.io). StartupObject in the csproj points here.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
