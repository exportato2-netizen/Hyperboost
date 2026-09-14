using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HyperBoost;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperBoost");
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "crash.log"),
                $"{DateTime.Now:O} {e.Exception.GetType().FullName}: {e.Exception.Message}{Environment.NewLine}{e.Exception.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // El registro de errores nunca debe provocar un segundo fallo.
        }
    }
}
