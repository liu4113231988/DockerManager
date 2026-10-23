using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DockerManager.Wpf;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockerManager");

    private static readonly string CrashLogPath = Path.Combine(LogDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Keeps the window alive after an unexpected UI failure and leaves a stack trace on disk,
    /// so intermittent problems can be diagnosed instead of only being screenshotted.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog("UI", e.Exception);
        MessageBox.Show(
            "发生未处理的界面异常。程序会继续运行，详细信息已记录到：\n" + CrashLogPath + "\n\n" + e.Exception.Message,
            "DockerManager", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteLog("AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Background SSH/log tasks may fault after everyone stopped awaiting them.
        WriteLog("Task", e.Exception);
        e.SetObserved();
    }

    private static void WriteLog(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var entry = new StringBuilder()
                .AppendLine("=== " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " [" + source + "]")
                .AppendLine(ex?.ToString() ?? "(no exception object)")
                .AppendLine()
                .ToString();
            File.AppendAllText(CrashLogPath, entry, Encoding.UTF8);
        }
        catch
        {
            // Logging must never become the reason the app fails.
        }
    }
}
