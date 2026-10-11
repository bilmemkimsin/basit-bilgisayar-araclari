using System.Diagnostics;
using BasitBilgisayarAraclari.App.Services.FileOperations;

namespace BasitBilgisayarAraclari.Tests;

public sealed class SevenZipWorkerJobTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JobTerminationOrClose_StopsAssignedArchiveWorker(bool disposeJob)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "BasitBilgisayarAraclari.App.exe");
        Assert.True(File.Exists(workerPath), $"Expected apphost at {workerPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("--archive-worker");
        startInfo.ArgumentList.Add("BBA7z" + Guid.NewGuid().ToString("N"));

        using var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the archive worker apphost.");
        var job = new SevenZipWorkerJob(1024L * 1024 * 1024);
        try
        {
            job.Assign(worker);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Assert.False(worker.HasExited, "Worker exited before the job lifecycle could be checked.");

            if (disposeJob)
            {
                job.Dispose();
            }
            else
            {
                job.Terminate();
            }

            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.HasExited);
        }
        finally
        {
            job.Dispose();
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
