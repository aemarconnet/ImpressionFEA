using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ImpressionFEA.App;

public partial class App : Application
{
    public static string CrashLogPath { get; } = Path.Combine(AppContext.BaseDirectory, "impressionfea-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrash(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails were written to:\n{CrashLogPath}",
            "ImpressionFEA", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void WriteCrash(Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException)
        {
            // Nothing more we can do; the message box still informs the operator.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
