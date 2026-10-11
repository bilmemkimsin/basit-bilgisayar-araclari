namespace BasitBilgisayarAraclari.App.Services;

public interface ITrayIconService : IDisposable
{
    event EventHandler? ShowWindowRequested;

    event EventHandler? CancelTimerRequested;

    event EventHandler? ExitRequested;

    void SetTimerScheduled(bool isScheduled);
}
