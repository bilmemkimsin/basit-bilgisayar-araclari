using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Localization;
using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.App.Services.FileOperations;
using BasitBilgisayarAraclari.App.ViewModels;
using WpfApplication = System.Windows.Application;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace BasitBilgisayarAraclari.App;

public partial class App : WpfApplication
{
    private const string InstanceMutexName = @"Local\BasitBilgisayarAraclari.SingleInstance";
    private const string ActivateInstanceEventName = @"Local\BasitBilgisayarAraclari.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateInstanceEvent;
    private WpfDispatcherTimer? _activationPollTimer;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        var preferencesStore = new UserPreferencesStore();
        var preferences = preferencesStore.Load();
        if (SevenZipWorkerHost.IsWorkerInvocation(e.Args))
        {
            LocalizationManager.Instance.SetLanguage(Environment.GetEnvironmentVariable("BBA_UI_LANGUAGE"));
        }
        else
        {
            LocalizationManager.Instance.SetLanguage(preferences.LanguageCode);
            ThemeManager.Apply(preferences.Theme);
        }

        if (SevenZipWorkerHost.IsWorkerInvocation(e.Args))
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            try
            {
                var exitCode = await SevenZipWorkerHost.RunAsync(e.Args[1]);
                Environment.ExitCode = exitCode;
                Shutdown(exitCode);
            }
            catch
            {
                Environment.ExitCode = 2;
                Shutdown(2);
            }

            return;
        }

        if (!TryBecomePrimaryInstance())
        {
            base.OnStartup(e);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var splash = new SplashWindow();
        await splash.ShowForStartupAsync();

        var shutdownService = new WindowsShutdownService();
        var fileOperations = new FileOperationService();
        var profiles = new GameProfileStore();
        var dialogs = new FileOperationDialogService();
        var viewModel = new MainWindowViewModel(ToolRegistry.CreateBuiltIns(
            shutdownService,
            fileOperationService: fileOperations,
            gameProfileStore: profiles,
            fileOperationDialogs: dialogs),
            new AppSettingsViewModel(preferencesStore, preferences));
        var exitCoordinator = new ShutdownExitCoordinator(shutdownService);
        var window = new MainWindow(viewModel, exitCoordinator);
        MainWindow = window;
        window.Show();
        _activationPollTimer = new WpfDispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _activationPollTimer.Tick += (_, _) =>
        {
            if (_activateInstanceEvent?.WaitOne(0) == true)
            {
                window.RestoreFromTray();
            }
        };
        _activationPollTimer.Start();
        ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _activationPollTimer?.Stop();
        _activateInstanceEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private bool TryBecomePrimaryInstance()
    {
        _activateInstanceEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivateInstanceEventName);
        var mutex = new Mutex(
            initiallyOwned: true,
            InstanceMutexName,
            out var createdNew);
        if (createdNew)
        {
            _instanceMutex = mutex;
            return true;
        }

        _activateInstanceEvent.Set();
        _activateInstanceEvent.Dispose();
        _activateInstanceEvent = null;
        mutex.Dispose();
        return false;
    }
}
