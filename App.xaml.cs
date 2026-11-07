using System.Windows;

namespace AllyHub;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Updater.Cleanup();
        new Splash().Show();
    }
}
