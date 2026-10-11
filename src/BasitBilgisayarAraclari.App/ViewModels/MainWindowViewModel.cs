using System.Collections.ObjectModel;
using System.Windows.Input;
using BasitBilgisayarAraclari.App.Core;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Services;

namespace BasitBilgisayarAraclari.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private ToolWorkspaceViewModel? _activeWorkspace;

    public MainWindowViewModel(ToolRegistry registry, AppSettingsViewModel? settings = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        Tools = new ObservableCollection<ToolCardViewModel>(
            registry.Modules.Select(module => new ToolCardViewModel(module)));
        Settings = settings ?? new AppSettingsViewModel(new UserPreferencesStore(), new UserPreferences());

        ShutdownTimer = registry.Find("shutdown-timer")?.CreateWorkspace() as ShutdownTimerViewModel;
        if (ShutdownTimer is not null)
        {
            ShutdownTimer.PropertyChanged += OnShutdownTimerPropertyChanged;
        }

        OpenToolCommand = new RelayCommand(OpenTool, parameter => parameter is ToolCardViewModel);
        ShowDashboardCommand = new RelayCommand(_ => ActiveWorkspace = null);
    }

    public ObservableCollection<ToolCardViewModel> Tools { get; }

    public AppSettingsViewModel Settings { get; }

    public ShutdownTimerViewModel? ShutdownTimer { get; }

    public bool IsShutdownTimerScheduled => ShutdownTimer?.IsScheduled == true;

    public bool IsShutdownTimerBusy => ShutdownTimer?.IsBusy == true;

    public ToolWorkspaceViewModel? ActiveWorkspace
    {
        get => _activeWorkspace;
        private set
        {
            if (SetProperty(ref _activeWorkspace, value))
            {
                OnPropertyChanged(nameof(IsWorkspaceVisible));
            }
        }
    }

    public bool IsWorkspaceVisible => ActiveWorkspace is not null;

    public ICommand OpenToolCommand { get; }

    public ICommand ShowDashboardCommand { get; }

    private void OpenTool(object? parameter)
    {
        if (parameter is ToolCardViewModel tool)
        {
            ActiveWorkspace = tool.Module.CreateWorkspace();
        }
    }

    private void OnShutdownTimerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShutdownTimerViewModel.IsScheduled))
        {
            OnPropertyChanged(nameof(IsShutdownTimerScheduled));
        }
        else if (e.PropertyName == nameof(ShutdownTimerViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(IsShutdownTimerBusy));
        }
    }

    public void Dispose()
    {
        ShutdownTimer?.PropertyChanged -= OnShutdownTimerPropertyChanged;
        foreach (var tool in Tools)
        {
            tool.Dispose();
        }

        foreach (var module in Tools.Select(tool => tool.Module))
        {
            if (module.CreateWorkspace() is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

}
