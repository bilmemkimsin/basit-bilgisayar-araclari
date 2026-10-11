namespace BasitBilgisayarAraclari.App.Services;

public enum ShutdownExitChoice
{
    ContinueScheduledShutdown,
    CancelScheduledShutdown,
    StayInApplication
}

public enum WindowCloseDisposition
{
    HideToTray,
    CloseApplication
}

public static class TrayLifecyclePolicy
{
    public static WindowCloseDisposition ForWindowClose(bool exitApproved) =>
        exitApproved ? WindowCloseDisposition.CloseApplication : WindowCloseDisposition.HideToTray;
}

public sealed class ShutdownExitCoordinator(IShutdownService shutdownService)
{
    public async Task<bool> CanExitAsync(
        bool hasScheduledShutdown,
        bool operationInProgress,
        Func<Task<ShutdownExitChoice>> chooseExitAction)
    {
        ArgumentNullException.ThrowIfNull(chooseExitAction);

        if (operationInProgress)
        {
            return false;
        }

        if (!hasScheduledShutdown)
        {
            return true;
        }

        return await chooseExitAction().ConfigureAwait(false) switch
        {
            ShutdownExitChoice.ContinueScheduledShutdown => true,
            ShutdownExitChoice.CancelScheduledShutdown => await CancelAndExitAsync().ConfigureAwait(false),
            ShutdownExitChoice.StayInApplication => false,
            _ => throw new ArgumentOutOfRangeException(nameof(chooseExitAction), "Bilinmeyen çıkış seçeneği.")
        };
    }

    private async Task<bool> CancelAndExitAsync()
    {
        await shutdownService.CancelAsync().ConfigureAwait(false);
        return true;
    }
}
