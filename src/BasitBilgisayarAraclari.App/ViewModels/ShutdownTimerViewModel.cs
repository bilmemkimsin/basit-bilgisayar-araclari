using System.Globalization;
using BasitBilgisayarAraclari.App.Core;
using BasitBilgisayarAraclari.App.Localization;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Services;

namespace BasitBilgisayarAraclari.App.ViewModels;

public sealed class ShutdownTimerViewModel : ToolWorkspaceViewModel
{
    private readonly IShutdownService _shutdownService;
    private readonly TimeProvider _timeProvider;
    private string _hoursInput = "0";
    private string _minutesInput = "0";
    private string _secondsInput = "0";
    private bool _isRiskAcknowledged;
    private bool _isScheduled;
    private bool _isBusy;
    private long _startedAtTimestamp;
    private TimeSpan _scheduledDuration;
    private TimeSpan _remainingTime;
    private string _remainingTimeText = "00:00:00";
    private string _errorMessage = string.Empty;
    private CultureInfo _errorCulture = CultureInfo.GetCultureInfo("tr-TR");

    public ShutdownTimerViewModel(
        IShutdownService shutdownService,
        TimeProvider? timeProvider = null,
        string toolLabel = "ZAMANLAYICI")
        : base(
            "Zamanlı Kapatma",
            "Bilgisayarın ne zaman kapanacağını saat, dakika ve saniye olarak seç.",
            "Süreyi doğrula, veri kaybı uyarısını onayla ve Windows zamanlayıcısını yönet.",
            "◷",
            "#EEEAFE",
            "#6657C8",
            toolLabel)
    {
        ArgumentNullException.ThrowIfNull(shutdownService);
        _shutdownService = shutdownService;
        _timeProvider = timeProvider ?? TimeProvider.System;

        ScheduleCommand = new AsyncRelayCommand(
            _ => ScheduleAsync(),
            _ => CanSchedule);
        CancelCommand = new AsyncRelayCommand(
            _ => CancelAsync(),
            _ => IsScheduled && !IsBusy);
    }

    public string HoursInput
    {
        get => _hoursInput;
        set
        {
            if (SetProperty(ref _hoursInput, value))
            {
                NotifyInputChanged();
            }
        }
    }

    public string MinutesInput
    {
        get => _minutesInput;
        set
        {
            if (SetProperty(ref _minutesInput, value))
            {
                NotifyInputChanged();
            }
        }
    }

    public string SecondsInput
    {
        get => _secondsInput;
        set
        {
            if (SetProperty(ref _secondsInput, value))
            {
                NotifyInputChanged();
            }
        }
    }

    public bool IsRiskAcknowledged
    {
        get => _isRiskAcknowledged;
        set
        {
            if (SetProperty(ref _isRiskAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanSchedule));
                ScheduleCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsScheduled
    {
        get => _isScheduled;
        private set
        {
            if (SetProperty(ref _isScheduled, value))
            {
                OnPropertyChanged(nameof(IsInputEnabled));
                OnPropertyChanged(nameof(CanSchedule));
                OnPropertyChanged(nameof(StatusMessage));
                ScheduleCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsInputEnabled));
                OnPropertyChanged(nameof(CanSchedule));
                ScheduleCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInputEnabled => !IsScheduled && !IsBusy;

    public bool IsDurationValid => TryGetDuration(out _, out _);

    public bool CanSchedule =>
        !IsScheduled && !IsBusy && IsRiskAcknowledged && IsDurationValid;

    public string InputValidationMessage =>
        TryGetDuration(out _, out var error) ? string.Empty : LocalizationManager.Instance.Translate(error);

    public string ConfirmationSummary =>
        TryGetDuration(out var duration, out var error)
            ? LocalizationManager.Instance.Format(
                "Onaylarsan Windows bilgisayarı {duration} sonra kapatacak.",
                new Dictionary<string, object?> { ["duration"] = FormatDuration(duration) })
            : LocalizationManager.Instance.Translate(error);

    public TimeSpan RemainingTime
    {
        get => _remainingTime;
        private set => SetProperty(ref _remainingTime, value);
    }

    public string RemainingTimeText
    {
        get => _remainingTimeText;
        private set => SetProperty(ref _remainingTimeText, value);
    }

    public string StatusMessage => IsScheduled
        ? LocalizationManager.Instance.Translate("Windows kapatma zamanlayıcısı etkin. Uygulamayı kapatsan da Windows sayacı sürer.")
        : LocalizationManager.Instance.Translate("Zamanlayıcı henüz etkin değil.");

    public string ErrorMessage
    {
        get => LocalizationManager.Instance.TranslateUserMessage(_errorMessage, _errorCulture);
        private set
        {
            _errorCulture = LocalizationManager.Instance.Culture;
            SetProperty(ref _errorMessage, value);
        }
    }

    public AsyncRelayCommand ScheduleCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public async Task ScheduleAsync()
    {
        if (!CanSchedule || !TryGetDuration(out var duration, out _))
        {
            return;
        }

        ErrorMessage = string.Empty;
        IsBusy = true;
        var startedAtTimestamp = _timeProvider.GetTimestamp();

        try
        {
            await _shutdownService.ScheduleAsync(duration);
            _scheduledDuration = duration;
            _startedAtTimestamp = startedAtTimestamp;
            RemainingTime = duration;
            RemainingTimeText = FormatClock(duration);
            IsScheduled = true;
            RefreshCountdown();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Kapatma zamanlayıcısı başlatılamadı: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CancelAsync()
    {
        if (!IsScheduled || IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;
        IsBusy = true;

        try
        {
            await _shutdownService.CancelAsync();
            IsScheduled = false;
            IsRiskAcknowledged = false;
            RemainingTime = TimeSpan.Zero;
            RemainingTimeText = FormatClock(TimeSpan.Zero);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Windows zamanlayıcısı iptal edilemedi: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshCountdown()
    {
        if (!IsScheduled)
        {
            return;
        }

        var elapsed = _timeProvider.GetElapsedTime(_startedAtTimestamp);
        var rawRemaining = _scheduledDuration - elapsed;
        var roundedSeconds = Math.Max(0, Math.Ceiling(rawRemaining.TotalSeconds));
        RemainingTime = TimeSpan.FromSeconds(roundedSeconds);
        RemainingTimeText = FormatClock(RemainingTime);
    }

    private void NotifyInputChanged()
    {
        OnPropertyChanged(nameof(IsDurationValid));
        OnPropertyChanged(nameof(InputValidationMessage));
        OnPropertyChanged(nameof(ConfirmationSummary));
        OnPropertyChanged(nameof(CanSchedule));
        ScheduleCommand.NotifyCanExecuteChanged();
    }

    private bool TryGetDuration(out TimeSpan duration, out string error)
    {
        duration = TimeSpan.Zero;

        if (!TryParseNumber(HoursInput, out var hours)
            || !TryParseNumber(MinutesInput, out var minutes)
            || !TryParseNumber(SecondsInput, out var seconds))
        {
            error = "Saat, dakika ve saniye alanlarına tam sayı gir.";
            return false;
        }

        if (minutes > 59 || seconds > 59)
        {
            error = "Dakika ve saniye değerleri 0 ile 59 arasında olmalı.";
            return false;
        }

        var totalSeconds = ((long)hours * 60 * 60) + (minutes * 60L) + seconds;
        if (totalSeconds == 0)
        {
            error = "Kapatma süresi sıfırdan büyük olmalı.";
            return false;
        }

        if (totalSeconds > WindowsShutdownService.MaximumDelaySeconds)
        {
            error = "En uzun kapatma süresi 10 yıl (315.360.000 saniye).";
            return false;
        }

        duration = TimeSpan.FromSeconds(totalSeconds);
        error = string.Empty;
        return true;
    }

    private static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static string FormatDuration(TimeSpan duration)
    {
        var hours = (long)duration.TotalHours;
        return LocalizationManager.Instance.Format(
            "{hours} saat, {minutes} dakika, {seconds} saniye",
            new Dictionary<string, object?>
            {
                ["hours"] = hours,
                ["minutes"] = duration.Minutes,
                ["seconds"] = duration.Seconds
            });
    }

    protected override void OnLanguageChanged(object? sender, EventArgs e)
    {
        base.OnLanguageChanged(sender, e);
        OnPropertyChanged(nameof(InputValidationMessage));
        OnPropertyChanged(nameof(ConfirmationSummary));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private static string FormatClock(TimeSpan duration)
    {
        var totalHours = (long)Math.Floor(duration.TotalHours);
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}
