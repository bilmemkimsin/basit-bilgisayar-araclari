using System.IO;
using System.Globalization;

namespace BasitBilgisayarAraclari.App.Services;

public sealed class WindowsShutdownService(IProcessRunner? processRunner = null) : IShutdownService
{
    public const long MaximumDelaySeconds = 315_360_000;

    private readonly IProcessRunner _processRunner = processRunner ?? new SystemProcessRunner();

    public Task ScheduleAsync(TimeSpan delay)
    {
        var seconds = delay.TotalSeconds;
        if (seconds is < 1 or > MaximumDelaySeconds || seconds != Math.Truncate(seconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(delay),
                "Kapatma süresi 1 ile 315.360.000 tam saniye arasında olmalıdır.");
        }

        return RunShutdownCommandAsync(
            ["/s", "/t", seconds.ToString("0", CultureInfo.InvariantCulture)]);
    }

    public Task CancelAsync() => RunShutdownCommandAsync(["/a"]);

    private async Task RunShutdownCommandAsync(IReadOnlyList<string> arguments)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            throw new InvalidOperationException("Windows sistem klasörü bulunamadı.");
        }

        var shutdownPath = Path.Combine(systemDirectory, "shutdown.exe");
        var exitCode = await _processRunner.RunAsync(shutdownPath, arguments).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Windows kapatma komutu başarısız oldu (çıkış kodu {exitCode}).");
        }
    }
}
