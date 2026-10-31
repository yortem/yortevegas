using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using YorteVegas.Services;

namespace YorteVegas;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);

        var settings = AppSettings.Load();

        string? startupProject = e.Args.FirstOrDefault(a =>
            File.Exists(a) && a.EndsWith(ProjectService.Extension, StringComparison.OrdinalIgnoreCase));
        var startupImports = e.Args
            .Where(a => File.Exists(a) && !a.EndsWith(ProjectService.Extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var window = new MainWindow(settings, startupProject, startupImports);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show(
            $"YorteVegas hit an unexpected error and will close.\n\n{e.Exception.Message}",
            "YorteVegas", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = false;
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YorteVegas");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}]\n{ex}\n\n");
        }
        catch
        {
            // Never mask the original failure.
        }
    }
}
