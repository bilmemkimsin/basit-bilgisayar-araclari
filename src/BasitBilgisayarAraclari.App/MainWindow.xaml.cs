using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using BasitBilgisayarAraclari.App.Localization;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.App.Services.FileOperations;
using BasitBilgisayarAraclari.App.ViewModels;
using WpfWindow = System.Windows.Window;

namespace BasitBilgisayarAraclari.App;

public partial class MainWindow : WpfWindow
{
    public MainWindow()
        : this(CreateDefaultServices())
    {
    }

    private MainWindow(WindowServices services)
        : this(services.ViewModel, services.ExitCoordinator)
    {
    }

    public MainWindow(MainWindowViewModel viewModel, ShutdownExitCoordinator exitCoordinator)
        : this(viewModel, exitCoordinator, trayIcon: null, chooseExitAction: null)
    {
    }

    public MainWindow(
        MainWindowViewModel viewModel,
        ShutdownExitCoordinator exitCoordinator,
        ITrayIconService? trayIcon,
        Func<Task<ShutdownExitChoice>>? chooseExitAction)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(exitCoordinator);

        InitializeComponent();
        ThemeManager.RegisterWindow(this);
        DataContext = viewModel;

        _viewModel = viewModel;
        _exitCoordinator = exitCoordinator;
        _trayIcon = trayIcon ?? new TrayIconService();
        _chooseExitAction = chooseExitAction ?? ChooseExitActionAsync;
        _trayIcon.ShowWindowRequested += OnTrayShowWindowRequested;
        _trayIcon.CancelTimerRequested += OnTrayCancelTimerRequested;
        _trayIcon.ExitRequested += OnTrayExitRequested;
        _trayIcon.SetTimerScheduled(_viewModel.IsShutdownTimerScheduled);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();
        Closing += OnWindowClosing;
    }

    private readonly MainWindowViewModel _viewModel;
    private readonly ShutdownExitCoordinator _exitCoordinator;
    private readonly ITrayIconService _trayIcon;
    private readonly DispatcherTimer _countdownTimer;
    private readonly Func<Task<ShutdownExitChoice>> _chooseExitAction;
    private bool _exitApproved;

    private static WindowServices CreateDefaultServices()
    {
        var shutdownService = new WindowsShutdownService();
        var fileOperations = new FileOperationService();
        var profiles = new GameProfileStore();
        var dialogs = new FileOperationDialogService();
        var viewModel = new MainWindowViewModel(ToolRegistry.CreateBuiltIns(
            shutdownService,
            fileOperationService: fileOperations,
            gameProfileStore: profiles,
            fileOperationDialogs: dialogs));
        return new WindowServices(viewModel, new ShutdownExitCoordinator(shutdownService));
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        var disposition = TrayLifecyclePolicy.ForWindowClose(_exitApproved);
        if (disposition == WindowCloseDisposition.HideToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _countdownTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _trayIcon.Dispose();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        if (_viewModel.ShutdownTimer is { IsScheduled: true } timer)
        {
            timer.RefreshCountdown();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActiveWorkspace))
        {
            Dispatcher.BeginInvoke(
                new Action(WorkspaceScrollViewer.ScrollToTop),
                DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsShutdownTimerScheduled))
        {
            _trayIcon.SetTimerScheduled(_viewModel.IsShutdownTimerScheduled);
        }
    }

    private void OnTrayShowWindowRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RestoreFromTray));

    private void OnTrayCancelTimerRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(async () => await CancelTimerFromTrayAsync()));

    private void OnTrayExitRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(async () => await ExitFromTrayAsync()));

    internal void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async Task CancelTimerFromTrayAsync()
    {
        if (_viewModel.ShutdownTimer is not { } timer)
        {
            return;
        }

        await timer.CancelCommand.ExecuteAsync();
        if (!string.IsNullOrWhiteSpace(timer.ErrorMessage))
        {
            RestoreFromTray();
            System.Windows.MessageBox.Show(
                this,
                LocalizationManager.Instance.TranslateUserMessage(timer.ErrorMessage),
                LocalizationManager.Instance.Translate("Zamanlayıcı iptal edilemedi"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ExitFromTrayAsync()
    {
        if (_viewModel.IsShutdownTimerBusy)
        {
            RestoreFromTray();
            System.Windows.MessageBox.Show(
                this,
                LocalizationManager.Instance.Translate("Zamanlayıcı işlemi sürüyor. İşlem tamamlandıktan sonra tekrar çıkış yapabilirsin."),
                LocalizationManager.Instance.Translate("Zamanlayıcı işlemi sürüyor"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var canExit = await _exitCoordinator.CanExitAsync(
                _viewModel.IsShutdownTimerScheduled,
                _viewModel.IsShutdownTimerBusy,
                _chooseExitAction);
            if (!canExit)
            {
                return;
            }

            _exitApproved = true;
            Close();
        }
        catch (Exception exception)
        {
            RestoreFromTray();
            System.Windows.MessageBox.Show(
                this,
                LocalizationManager.Instance.TranslateUserMessage($"Uygulamadan çıkış tamamlanamadı: {exception.Message}"),
                LocalizationManager.Instance.Translate("Çıkış başarısız"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private Task<ShutdownExitChoice> ChooseExitActionAsync()
    {
        RestoreFromTray();
        var dialog = new ShutdownExitDialog { Owner = this };
        dialog.ShowDialog();
        return Task.FromResult(dialog.Choice);
    }

    private sealed record WindowServices(
        MainWindowViewModel ViewModel,
        ShutdownExitCoordinator ExitCoordinator);
}
