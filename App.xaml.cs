using System.Windows;

namespace MediaController;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Evita abrir dos copias del widget a la vez.
        _mutex = new Mutex(true, "MediaController_SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
