using System.Windows.Input;

namespace BasitBilgisayarAraclari.App.Core;

public sealed class AsyncRelayCommand(
    Func<object?, Task> execute,
    Predicate<object?>? canExecute = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter) =>
        !_isRunning && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        NotifyCanExecuteChanged();

        try
        {
            await execute(parameter);
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
