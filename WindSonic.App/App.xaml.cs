using System.IO;
using System.Windows;
using System.Windows.Threading;
using WindSonic.App.Services;
using WindSonic.App.ViewModels;

namespace WindSonic.App;

public partial class App : Application
{
    private NativeAudioPlayerService? _audioPlayerService;
    private bool _fatalErrorShown;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            var settingsStore = new SettingsStore();
            var spotifyService = new SpotifyService();
            var youTubeAudioResolver = new YouTubeAudioResolverService();
            _audioPlayerService = new NativeAudioPlayerService();
            var audioCacheService = new AudioCacheService();
            var spotifyPlaylistImportService = new SpotifyPlaylistImportService();

            var viewModel = new MainWindowViewModel(
                settingsStore,
                spotifyService,
                spotifyPlaylistImportService,
                youTubeAudioResolver,
                _audioPlayerService,
                audioCacheService);

            var mainWindow = new MainWindow(viewModel);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ShowFatalStartupError("WindSonic failed during startup.", ex);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _audioPlayerService?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalStartupError("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        Shutdown(-2);
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowFatalStartupError("Unhandled application exception.", ex);
        }
        else
        {
            ShowFatalStartupError("Unhandled application exception.", new Exception("Non-Exception unhandled error."));
        }
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ShowFatalStartupError("Background task exception.", e.Exception);
        e.SetObserved();
    }

    private void ShowFatalStartupError(string title, Exception ex)
    {
        try
        {
            LogFatal(title, ex);
        }
        catch
        {
            // Ignore logging failures.
        }

        if (_fatalErrorShown)
        {
            return;
        }

        _fatalErrorShown = true;

        try
        {
            MessageBox.Show(
                $"{title}\n\n{ex.Message}\n\nA full error log was written to %AppData%\\WindSonic\\startup-error.log",
                "WindSonic Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // If WPF message box cannot be shown, rely on the log file.
        }
    }

    private static void LogFatal(string title, Exception ex)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "WindSonic");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "startup-error.log");
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(path, text);
    }
}

