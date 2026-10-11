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

        // Jobs from CAD (SolidWorks add-in): hand them to the running window if there is one.
        var args = e.Args;
        bool capture = args.Contains("--capture");
        bool job = args.Contains("--job") || args.Contains("--configs");
        if (!capture && job && Infrastructure.SingleInstance.TryForward(args))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (!capture)
        {
            Infrastructure.SingleInstance.Listen(a => Dispatcher.InvokeAsync(() => window.HandleExternalAsync(a)), _listen.Token);
            // After the window has loaded and rendered (Loaded can fire during Show, before a handler could attach).
            if (job) Dispatcher.InvokeAsync(() => window.HandleExternalAsync(args), DispatcherPriority.ApplicationIdle);
        }
    }

    private readonly CancellationTokenSource _listen = new();

    protected override void OnExit(ExitEventArgs e)
    {
        _listen.Cancel();
        _listen.Dispose();
        base.OnExit(e);
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
