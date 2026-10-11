using BasitBilgisayarAraclari.App.Services;

namespace BasitBilgisayarAraclari.Tests;

public sealed class WindowsShutdownServiceTests
{
    [Fact]
    public async Task SchedulingUsesTheLocalWindowsCommandWithSeparatedArguments()
    {
        var runner = new FakeProcessRunner();
        var service = new WindowsShutdownService(runner);

        await service.ScheduleAsync(TimeSpan.FromSeconds(75));

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
            runner.ExecutablePath);
        Assert.Equal(["/s", "/t", "75"], runner.Arguments);
    }

    [Fact]
    public async Task CancellationUsesAbortArgument()
    {
        var runner = new FakeProcessRunner();
        var service = new WindowsShutdownService(runner);

        await service.CancelAsync();

        Assert.Equal(["/a"], runner.Arguments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(315_360_001)]
    public async Task InvalidScheduleIsRejectedWithoutStartingAProcess(long seconds)
    {
        var runner = new FakeProcessRunner();
        var service = new WindowsShutdownService(runner);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.ScheduleAsync(TimeSpan.FromSeconds(seconds)));

        Assert.Null(runner.ExecutablePath);
    }

    [Fact]
    public async Task NonzeroProcessExitCodeIsReported()
    {
        var runner = new FakeProcessRunner { ExitCode = 5 };
        var service = new WindowsShutdownService(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ScheduleAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("çıkış kodu 5", error.Message);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string? ExecutablePath { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public int ExitCode { get; init; }

        public Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments)
        {
            ExecutablePath = executablePath;
            Arguments = arguments.ToArray();
            return Task.FromResult(ExitCode);
        }
    }
}
