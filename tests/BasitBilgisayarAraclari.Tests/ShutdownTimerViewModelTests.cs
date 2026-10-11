using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.App.ViewModels;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.Tests.Fakes;

namespace BasitBilgisayarAraclari.Tests;

public sealed class ShutdownTimerViewModelTests
{
    [Fact]
    public void ZeroDurationIsRejected()
    {
        var (viewModel, service, _) = CreateViewModel();

        Assert.False(viewModel.IsDurationValid);
        Assert.False(viewModel.ScheduleCommand.CanExecute(null));
        Assert.Contains("sıfırdan büyük", viewModel.InputValidationMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(service.ScheduledDelays);
    }

    [Theory]
    [InlineData("a", "0", "1")]
    [InlineData("0", "60", "0")]
    [InlineData("0", "0", "60")]
    [InlineData("-1", "0", "1")]
    [InlineData("87600", "0", "1")]
    public void InvalidAndOutOfRangeInputsAreRejected(string hours, string minutes, string seconds)
    {
        var (viewModel, service, _) = CreateViewModel();
        viewModel.HoursInput = hours;
        viewModel.MinutesInput = minutes;
        viewModel.SecondsInput = seconds;

        Assert.False(viewModel.IsDurationValid);
        Assert.False(viewModel.ScheduleCommand.CanExecute(null));
        Assert.Empty(service.ScheduledDelays);
    }

    [Fact]
    public async Task ScheduleConvertsHoursMinutesAndSecondsAndRequiresRiskAcknowledgement()
    {
        var (viewModel, service, _) = CreateViewModel();
        viewModel.HoursInput = "2";
        viewModel.MinutesInput = "3";
        viewModel.SecondsInput = "4";

        Assert.True(viewModel.IsDurationValid);
        Assert.False(viewModel.ScheduleCommand.CanExecute(null));

        viewModel.IsRiskAcknowledged = true;
        Assert.True(viewModel.ScheduleCommand.CanExecute(null));
        await viewModel.ScheduleCommand.ExecuteAsync();

        Assert.Equal(TimeSpan.FromSeconds(7_384), Assert.Single(service.ScheduledDelays));
        Assert.True(viewModel.IsScheduled);
        Assert.Equal("02:03:04", viewModel.RemainingTimeText);
        Assert.Contains("Windows bilgisayarı 2 saat, 3 dakika, 4 saniye sonra kapatacak", viewModel.ConfirmationSummary);
    }

    [Fact]
    public async Task CountdownUsesElapsedTimeAndUpdatesRemainingDisplay()
    {
        var (viewModel, _, clock) = CreateViewModel();
        viewModel.MinutesInput = "1";
        viewModel.SecondsInput = "5";
        viewModel.IsRiskAcknowledged = true;
        await viewModel.ScheduleAsync();

        clock.Advance(TimeSpan.FromSeconds(6));
        viewModel.RefreshCountdown();

        Assert.Equal(TimeSpan.FromSeconds(59), viewModel.RemainingTime);
        Assert.Equal("00:00:59", viewModel.RemainingTimeText);
    }

    [Fact]
    public async Task CancelCallsServiceAndResetsScheduleState()
    {
        var (viewModel, service, _) = CreateViewModel();
        viewModel.SecondsInput = "30";
        viewModel.IsRiskAcknowledged = true;
        await viewModel.ScheduleAsync();

        await viewModel.CancelCommand.ExecuteAsync();

        Assert.Equal(1, service.CancellationCount);
        Assert.False(viewModel.IsScheduled);
        Assert.False(viewModel.IsRiskAcknowledged);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedScheduleDoesNotPretendShutdownWasScheduled()
    {
        var (viewModel, service, _) = CreateViewModel();
        service.ScheduleException = new InvalidOperationException("izin reddedildi");
        viewModel.SecondsInput = "10";
        viewModel.IsRiskAcknowledged = true;

        await viewModel.ScheduleAsync();

        Assert.False(viewModel.IsScheduled);
        Assert.Contains("izin reddedildi", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task MainWindowExposesBusyStateWhileScheduleIsPending()
    {
        var service = new BlockingShutdownService();
        var mainWindow = new MainWindowViewModel(ToolRegistry.CreateBuiltIns(service));
        var busyTransitions = new List<bool>();
        mainWindow.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsShutdownTimerBusy))
            {
                busyTransitions.Add(mainWindow.IsShutdownTimerBusy);
            }
        };
        var timer = Assert.IsType<ShutdownTimerViewModel>(mainWindow.ShutdownTimer);
        timer.SecondsInput = "10";
        timer.IsRiskAcknowledged = true;

        var scheduleTask = timer.ScheduleAsync();

        Assert.True(mainWindow.IsShutdownTimerBusy);
        service.CompleteSchedule();
        await scheduleTask;

        Assert.False(mainWindow.IsShutdownTimerBusy);
        Assert.Equal([true, false], busyTransitions);
    }

    private static (ShutdownTimerViewModel ViewModel, FakeShutdownService Service, FakeTimeProvider Clock)
        CreateViewModel()
    {
        var service = new FakeShutdownService();
        var clock = new FakeTimeProvider();
        return (new ShutdownTimerViewModel(service, clock), service, clock);
    }

    private sealed class BlockingShutdownService : IShutdownService
    {
        private readonly TaskCompletionSource _scheduleCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ScheduleAsync(TimeSpan delay) => _scheduleCompletion.Task;

        public Task CancelAsync() => Task.CompletedTask;

        public void CompleteSchedule() => _scheduleCompletion.SetResult();
    }
}
