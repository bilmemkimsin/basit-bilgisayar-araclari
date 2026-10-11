using System.Diagnostics;

namespace BasitBilgisayarAraclari.App.Services;

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows komutu başlatılamadı.");

        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}
