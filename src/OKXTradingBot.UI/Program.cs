using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.ReactiveUI;
using OKXTradingBot.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException      += OnUnobservedTaskException;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main", ex);
            ShowFatalError(ex);
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: 창 없이 종료되지 않도록 메시지박스 표시
                MessageBox(IntPtr.Zero,
                    $"앱 시작 실패:\n{ex.Message}\n\n로그: %USERPROFILE%\\.okxtradingbot\\logs\\crash.log",
                    "Orion — 시작 오류", 0x10);
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => WriteCrashLog("AppDomain", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("Task", e.Exception);
        e.SetObserved();
    }

    internal static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".okxtradingbot", "logs");
            Directory.CreateDirectory(logDir);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH ({source})");
            AppendException(sb, ex, 0);
            sb.AppendLine("============================================================");
            sb.AppendLine();

            File.AppendAllText(Path.Combine(logDir, "crash.log"), sb.ToString());
        }
        catch { }
    }

    private static void AppendException(System.Text.StringBuilder sb, Exception? ex, int depth)
    {
        if (ex is null) return;
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}{ex.GetType().FullName}: {ex.Message}");
        if (ex.StackTrace is not null)
            foreach (var line in ex.StackTrace.Split('\n'))
                sb.AppendLine($"{indent}  {line.TrimEnd()}");
        if (ex.InnerException is not null)
        {
            sb.AppendLine($"{indent}--- Inner ---");
            AppendException(sb, ex.InnerException, depth + 1);
        }
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine($"{indent}--- Aggregate ---");
                AppendException(sb, inner, depth + 1);
            }
    }
}
