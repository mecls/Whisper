using Velopack;

namespace Spit.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must be the first line (rule 44): Velopack's install/uninstall hooks run here and exit.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
