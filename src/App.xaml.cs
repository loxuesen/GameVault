using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace GameVault;

public partial class App : Application
{
    /// <summary>Per-session name that marks the one running copy.</summary>
    private const string InstanceMutexName = @"Local\GameVault.SingleInstance";

    /// <summary>Per-session event a second launch sets to ask for the window.</summary>
    private const string ShowSignalName = @"Local\GameVault.ShowWindow";

    /// <summary>Set while a fatal error dialog is already open, so a cascade cannot stack dialogs.</summary>
    private bool _reporting;

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report(args.ExceptionObject as Exception, fatal: true);

        // A copy already running in the background owns the window, so this launch just asks it
        // to come back. Without this, hiding to the tray would leave the app unreachable if the
        // notification-area icon is tucked away in the overflow.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            RequestShowFromRunningCopy();
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(ListenForShowRequests) { IsBackground = true, Name = "show-window-listener" }.Start();

        var window = new MainWindow();
        MainWindow = window;
        if (window.StartHidden)
        {
            // Transparent during startup so nothing can flash on screen before it hides itself.
            window.Opacity = 0;
            window.ShowInTaskbar = false;
        }
        window.Show();
    }

    /// <summary>Asks the already-running copy to bring its window back.</summary>
    private static void RequestShowFromRunningCopy()
    {
        try
        {
            EventWaitHandle.OpenExisting(ShowSignalName).Set();
        }
        catch (Exception)
        {
            // The running copy is mid-startup or already gone; nothing useful to signal.
        }
    }

    /// <summary>Waits for later launches asking for the window, and answers them on the UI thread.</summary>
    private void ListenForShowRequests()
    {
        while (true)
        {
            try
            {
                if (_showSignal is null || !_showSignal.WaitOne()) return;
            }
            catch (Exception)
            {
                return;
            }
            Dispatcher.Invoke(() => (MainWindow as MainWindow)?.ShowFromBackground());
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Keeps the window alive after an unexpected UI-thread failure and shows the
    /// error, which is more useful to a desktop user than a silent crash.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, fatal: false);
        e.Handled = true;
    }

    private void Report(Exception? error, bool fatal)
    {
        if (_reporting) return;
        _reporting = true;
        try
        {
            // XAML failures wrap the useful message in an inner exception, so unwrap them all.
            var text = new System.Text.StringBuilder();
            var current = error;
            for (var depth = 0; current is not null && depth < 6; depth++)
            {
                text.AppendLine(depth == 0 ? current.Message : $"[内层 {depth}] {current.Message}");
                current = current.InnerException;
            }
            text.AppendLine().Append(error?.StackTrace);

            MessageBox.Show(
                $"发生了一个错误{(fatal ? "，程序即将退出" : "")}：\n\n{text}",
                "一切游戏管理家", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _reporting = false;
        }
        if (fatal) Shutdown(1);
    }
}
