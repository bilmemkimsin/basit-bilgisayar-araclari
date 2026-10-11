using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.Tests.Fakes;

namespace BasitBilgisayarAraclari.Tests;

public sealed class ShutdownExitCoordinatorTests
{
    [Theory]
    [InlineData(ShutdownExitChoice.ContinueScheduledShutdown, true, 0)]
    [InlineData(ShutdownExitChoice.CancelScheduledShutdown, true, 1)]
    [InlineData(ShutdownExitChoice.StayInApplication, false, 0)]
    public async Task ExitChoiceControlsApplicationAndSystemTimer(
        ShutdownExitChoice choice,
        bool expectedCanExit,
        int expectedCancellationCount)
    {
        var service = new FakeShutdownService();
        var coordinator = new ShutdownExitCoordinator(service);
        var promptCount = 0;

        var canExit = await coordinator.CanExitAsync(
            hasScheduledShutdown: true,
            operationInProgress: false,
            chooseExitAction: () =>
            {
                promptCount++;
                return Task.FromResult(choice);
            });

        Assert.Equal(expectedCanExit, canExit);
        Assert.Equal(expectedCancellationCount, service.CancellationCount);
        Assert.Equal(1, promptCount);
    }

    [Fact]
    public async Task NoActiveTimerExitsWithoutPromptOrCancellation()
    {
        var service = new FakeShutdownService();
        var coordinator = new ShutdownExitCoordinator(service);
        var promptCount = 0;

        var canExit = await coordinator.CanExitAsync(
            hasScheduledShutdown: false,
            operationInProgress: false,
            chooseExitAction: () =>
            {
                promptCount++;
                return Task.FromResult(ShutdownExitChoice.StayInApplication);
            });

        Assert.True(canExit);
        Assert.Equal(0, promptCount);
        Assert.Equal(0, service.CancellationCount);
    }

    [Fact]
    public async Task OperationInProgressBlocksExitWithoutPromptOrCancellation()
    {
        var service = new FakeShutdownService();
        var coordinator = new ShutdownExitCoordinator(service);
        var promptCount = 0;

        var canExit = await coordinator.CanExitAsync(
            hasScheduledShutdown: false,
            operationInProgress: true,
            chooseExitAction: () =>
            {
                promptCount++;
                return Task.FromResult(ShutdownExitChoice.ContinueScheduledShutdown);
            });

        Assert.False(canExit);
        Assert.Equal(0, promptCount);
        Assert.Equal(0, service.CancellationCount);
    }

    [Fact]
    public async Task ClosingWindowHidesToTrayUntilExitIsApproved()
    {
        Assert.Equal(WindowCloseDisposition.HideToTray, TrayLifecyclePolicy.ForWindowClose(exitApproved: false));
        Assert.Equal(WindowCloseDisposition.CloseApplication, TrayLifecyclePolicy.ForWindowClose(exitApproved: true));
    }
}
