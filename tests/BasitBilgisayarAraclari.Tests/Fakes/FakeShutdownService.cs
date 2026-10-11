using BasitBilgisayarAraclari.App.Services;

namespace BasitBilgisayarAraclari.Tests.Fakes;

internal sealed class FakeShutdownService : IShutdownService
{
    public List<TimeSpan> ScheduledDelays { get; } = [];

    public int CancellationCount { get; private set; }

    public Exception? ScheduleException { get; set; }

    public Exception? CancellationException { get; set; }

    public Task ScheduleAsync(TimeSpan delay)
    {
        if (ScheduleException is not null)
        {
            throw ScheduleException;
        }

        ScheduledDelays.Add(delay);
        return Task.CompletedTask;
    }

    public Task CancelAsync()
    {
        if (CancellationException is not null)
        {
            throw CancellationException;
        }

        CancellationCount++;
        return Task.CompletedTask;
    }
}
