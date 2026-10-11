namespace BasitBilgisayarAraclari.App.Services;

public interface IShutdownService
{
    Task ScheduleAsync(TimeSpan delay);

    Task CancelAsync();
}
