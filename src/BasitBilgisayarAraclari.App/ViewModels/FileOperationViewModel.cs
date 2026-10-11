using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using BasitBilgisayarAraclari.App.Core;
using BasitBilgisayarAraclari.App.Localization;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Services.FileOperations;

namespace BasitBilgisayarAraclari.App.ViewModels;

public sealed class FileOperationViewModel : ToolWorkspaceViewModel, IDisposable
{
    private readonly IFileOperationService _fileOperations;
    private readonly IFileOperationDialogService _dialogs;
    private readonly GameProfileStore _profiles;
    private StagedFileOperation? _stagedOperation;
    private string _targetDirectory = string.Empty;
    private string _profileName = string.Empty;
    private string _statusMessage = "Kaynak ve hedef seçerek başlayabilirsin.";
    private string _errorMessage = string.Empty;
    private FileOperationRollbackException? _rollbackException;
    private CultureInfo _statusCulture = CultureInfo.GetCultureInfo("tr-TR");
    private CultureInfo _errorCulture = CultureInfo.GetCultureInfo("tr-TR");
    private bool _skipSingleTopFolder;
    private bool _overwriteConflicts = true;
    private bool _isBusy;
    private bool _isDisposed;
    private GameProfile? _selectedProfile;
    private BackupSummary? _selectedBackup;

    public FileOperationViewModel(
        ToolDescriptor descriptor,
        FileOperationKind operationKind,
        IFileOperationService fileOperations,
        GameProfileStore profiles,
        IFileOperationDialogService dialogs)
        : base(
            descriptor.Title,
            operationKind == FileOperationKind.GameModInstall
                ? "Birden fazla mod arşivini veya klasörünü aynı hedefte birleştir; hedef yolları ve çakışmaları uygulamadan önce gör."
                : "Birden fazla arşivi veya normal klasörü tek hedefte birleştir; iç yolları koru ve sonucu uygulamadan önce incele.",
            operationKind == FileOperationKind.GameModInstall
                ? "Mod dosyaları yerel yedekle uygulanır ve değişmemiş durumdaysa geri yüklenebilir."
                : "ZIP, 7z, RAR, TAR, GZ, BZ2 ve XZ arşivleri ile normal klasörler desteklenir.",
            descriptor.IconGlyph,
            descriptor.IconBackground,
            descriptor.IconForeground,
            descriptor.ToolLabel)
    {
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        OperationKind = operationKind;
        IsGameModMode = operationKind == FileOperationKind.GameModInstall;

        AddArchivesCommand = new RelayCommand(_ => AddArchives(), _ => !IsBusy);
        AddFolderCommand = new RelayCommand(_ => AddFolder(), _ => !IsBusy);
        RemoveSourceCommand = new RelayCommand(RemoveSource, _ => !IsBusy);
        PickTargetCommand = new RelayCommand(_ => PickTarget(), _ => !IsBusy);
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => CanPreview && !IsBusy);
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply && !IsBusy);
        SaveProfileCommand = new RelayCommand(_ => SaveProfile(), _ => IsGameModMode && !IsBusy && !string.IsNullOrWhiteSpace(ProfileName) && !string.IsNullOrWhiteSpace(TargetDirectory));
        RestoreBackupCommand = new AsyncRelayCommand(_ => RestoreBackupAsync(), _ => IsGameModMode && SelectedBackup is not null && !IsBusy && !SelectedBackup.IsRestored);

        var recent = _profiles.GetRecentPaths();
        foreach (var source in recent.Sources.Where(path => File.Exists(path) || Directory.Exists(path)))
        {
            Sources.Add(source);
        }

        if (!string.IsNullOrWhiteSpace(recent.TargetDirectory))
        {
            TargetDirectory = recent.TargetDirectory;
        }

        if (IsGameModMode)
        {
            foreach (var profile in _profiles.GetProfiles())
            {
                GameProfiles.Add(profile);
            }

            RefreshBackups();
        }
    }

    public FileOperationKind OperationKind { get; }

    public bool IsGameModMode { get; }

    public ObservableCollection<string> Sources { get; } = [];

    public ObservableCollection<FilePreviewEntry> PreviewEntries { get; } = [];

    public ObservableCollection<GameProfile> GameProfiles { get; } = [];

    public ObservableCollection<BackupSummary> Backups { get; } = [];

    public RelayCommand AddArchivesCommand { get; }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveSourceCommand { get; }

    public RelayCommand PickTargetCommand { get; }

    public AsyncRelayCommand PreviewCommand { get; }

    public AsyncRelayCommand ApplyCommand { get; }

    public RelayCommand SaveProfileCommand { get; }

    public AsyncRelayCommand RestoreBackupCommand { get; }

    public string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            if (IsBusy)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _targetDirectory, value))
            {
                InvalidatePreview("Hedef değişti. Önizlemeyi yenile.");
                SaveProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (IsBusy)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _profileName, value))
            {
                SaveProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => LocalizationManager.Instance.TranslateUserMessage(_statusMessage, _statusCulture);
        private set
        {
            _statusCulture = LocalizationManager.Instance.Culture;
            SetProperty(ref _statusMessage, value);
        }
    }

    public string ErrorMessage
    {
        get => _rollbackException?.GetLocalizedMessage()
            ?? LocalizationManager.Instance.TranslateUserMessage(_errorMessage, _errorCulture);
        private set
        {
            _rollbackException = null;
            _errorCulture = LocalizationManager.Instance.Culture;
            SetProperty(ref _errorMessage, value);
        }
    }

    public bool SkipSingleTopFolder
    {
        get => _skipSingleTopFolder;
        set
        {
            if (IsBusy)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _skipSingleTopFolder, value))
            {
                InvalidatePreview("Yol seçeneği değişti. Önizlemeyi yenile.");
            }
        }
    }

    public bool OverwriteConflicts
    {
        get => _overwriteConflicts;
        set
        {
            if (IsBusy)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(SkipConflicts));
                return;
            }

            if (SetProperty(ref _overwriteConflicts, value))
            {
                OnPropertyChanged(nameof(SkipConflicts));
                OnPropertyChanged(nameof(ConflictPolicyDescription));
            }
        }
    }

    public bool SkipConflicts
    {
        get => !OverwriteConflicts;
        set => OverwriteConflicts = !value;
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsInputsEnabled));
                NotifyCommands();
            }
        }
    }

    public bool IsInputsEnabled => !IsBusy;

    public bool CanPreview => Sources.Count > 0 && !string.IsNullOrWhiteSpace(TargetDirectory);

    public bool CanApply => _stagedOperation is not null && PreviewEntries.Count > 0;

    public GameProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (IsBusy)
            {
                OnPropertyChanged();
                return;
            }

            if (!SetProperty(ref _selectedProfile, value) || value is null)
            {
                return;
            }

            ProfileName = value.Name;
            TargetDirectory = value.TargetDirectory;
            SkipSingleTopFolder = value.SkipSingleTopFolder;
        }
    }

    public BackupSummary? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (SetProperty(ref _selectedBackup, value))
            {
                RestoreBackupCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ConflictPolicyDescription => LocalizationManager.Instance.Translate(OverwriteConflicts
        ? "Çakışmada son seçilen kaynağın dosyası kullanılır. Mod aracı mevcut dosyayı yedekler."
        : "Farklı içerikli kaynak veya hedef dosyası olan yollar atlanır.");

    private void AddArchives()
    {
        foreach (var path in _dialogs.PickArchives())
        {
            AddSource(path);
        }
    }

    private void AddFolder()
    {
        var path = _dialogs.PickFolder("Kaynak klasörü seç");
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddSource(path);
        }
    }

    private void AddSource(string path)
    {
        if (!Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Sources.Add(path);
            InvalidatePreview("Kaynak listesi değişti. Önizlemeyi yenile.");
        }
    }

    private void RemoveSource(object? parameter)
    {
        if (parameter is string source && Sources.Remove(source))
        {
            InvalidatePreview("Kaynak listesi değişti. Önizlemeyi yenile.");
        }
    }

    private void PickTarget()
    {
        var path = _dialogs.PickFolder(IsGameModMode ? "Oyun klasörünü seç" : "Çıkarma hedefini seç");
        if (!string.IsNullOrWhiteSpace(path))
        {
            TargetDirectory = path;
        }
    }

    private async Task PreviewAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        DisposeStagedOperation();
        PreviewEntries.Clear();
        OnPropertyChanged(nameof(CanApply));
        var passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var passwordAttempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                try
                {
                    _stagedOperation = await _fileOperations.PreviewAsync(
                        OperationKind,
                        Sources.ToArray(),
                        TargetDirectory,
                        SkipSingleTopFolder,
                        passwords);
                    break;
                }
                catch (ArchivePasswordRequiredException exception)
                {
                    var attempts = passwordAttempts.GetValueOrDefault(exception.ArchivePath);
                    if (attempts >= 3)
                    {
                        throw new InvalidOperationException("Arşiv parolası üç denemede doğrulanamadı.", exception);
                    }

                    var password = _dialogs.AskArchivePassword(Path.GetFileName(exception.ArchivePath), exception.SuppliedPasswordWasRejected);
                    if (password is null)
                    {
                        StatusMessage = "Parola girişi iptal edildi. Hedefte hiçbir dosya değiştirilmedi.";
                        return;
                    }

                    passwords[exception.ArchivePath] = password;
                    passwordAttempts[exception.ArchivePath] = attempts + 1;
                }
            }

            foreach (var entry in _stagedOperation.Entries)
            {
                PreviewEntries.Add(entry);
            }

            OnPropertyChanged(nameof(CanApply));

            _profiles.RememberPaths(Sources, TargetDirectory);
            StatusMessage = PreviewEntries.Count == 0
                ? "Kaynaklarda çıkarılacak dosya bulunamadı."
                : $"{PreviewEntries.Count:N0} dosya yolu hazır. Çakışmaları inceleyip uygulayabilirsin.";
        }
        catch (Exception exception)
        {
            DisposeStagedOperation();
            ErrorMessage = exception.Message;
            StatusMessage = "Önizleme tamamlanamadı. Hedefte hiçbir dosya değiştirilmedi.";
        }
        finally
        {
            passwords.Clear();
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task ApplyAsync()
    {
        if (_stagedOperation is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var policy = OverwriteConflicts ? ConflictPolicy.OverwriteConflicts : ConflictPolicy.SkipConflicts;
            var result = await _fileOperations.ApplyAsync(_stagedOperation, policy, ProfileName);
            StatusMessage = $"İşlem tamamlandı: {result.AppliedFiles:N0} dosya uygulandı, {result.SkippedFiles:N0} atlandı, {result.BackedUpFiles:N0} dosya yedeklendi.";
            if (result.BackupId.HasValue)
            {
                RefreshBackups();
                SelectedBackup = Backups.FirstOrDefault(backup => backup.Id == result.BackupId.Value);
            }
        }
        catch (Exception exception)
        {
            if (exception is FileOperationRollbackException)
            {
                RefreshBackups();
            }

            ErrorMessage = exception.Message;
            if (exception is FileOperationRollbackException rollbackException)
            {
                _rollbackException = rollbackException;
                OnPropertyChanged(nameof(ErrorMessage));
            }
            StatusMessage = "İşlem tamamlanamadı. Uygulama mümkün olan değişiklikleri geri aldı.";
        }
        finally
        {
            DisposeStagedOperation();
            PreviewEntries.Clear();
            IsBusy = false;
            OnPropertyChanged(nameof(CanApply));
            NotifyCommands();
        }
    }

    private void SaveProfile()
    {
        try
        {
            var profileId = SelectedProfile?.Id;
            _profiles.SaveProfile(ProfileName, TargetDirectory, SkipSingleTopFolder, profileId);
            GameProfiles.Clear();
            foreach (var profile in _profiles.GetProfiles())
            {
                GameProfiles.Add(profile);
            }

            SelectedProfile = GameProfiles.FirstOrDefault(profile => string.Equals(profile.Name, ProfileName.Trim(), StringComparison.CurrentCultureIgnoreCase));
            StatusMessage = "Oyun profili bu bilgisayarda kaydedildi.";
            ErrorMessage = string.Empty;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task RestoreBackupAsync()
    {
        if (SelectedBackup is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _fileOperations.RestoreBackupAsync(SelectedBackup.Id);
            StatusMessage = result.IsFullyRestored
                ? $"Yedek geri yüklendi: {result.RestoredFiles:N0} dosya geri alındı."
                : $"{result.RestoredFiles:N0} dosya geri alındı; değişmiş {result.SkippedChangedFiles:N0} dosya güvenlik için atlandı. Geri yüklemeyi tekrar deneyebilirsin.";
            RefreshBackups();
            SelectedBackup = Backups.FirstOrDefault(backup => backup.Id == SelectedBackup.Id);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshBackups()
    {
        var previous = SelectedBackup?.Id;
        Backups.Clear();
        foreach (var backup in _fileOperations.GetBackups())
        {
            Backups.Add(backup);
        }

        SelectedBackup = Backups.FirstOrDefault(backup => backup.Id == previous);
    }

    private void InvalidatePreview(string message)
    {
        DisposeStagedOperation();
        PreviewEntries.Clear();
        StatusMessage = message;
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanApply));
        NotifyCommands();
    }

    private void DisposeStagedOperation()
    {
        _stagedOperation?.Dispose();
        _stagedOperation = null;
    }

    private void NotifyCommands()
    {
        AddArchivesCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        PickTargetCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged();
    }

    protected override void OnLanguageChanged(object? sender, EventArgs e)
    {
        base.OnLanguageChanged(sender, e);
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ConflictPolicyDescription));

        if (_stagedOperation is not null)
        {
            var entries = _stagedOperation.Entries;
            PreviewEntries.Clear();
            foreach (var entry in entries)
            {
                PreviewEntries.Add(entry);
            }
        }

        if (Backups.Count > 0)
        {
            var selectedId = SelectedBackup?.Id;
            var backups = Backups.ToArray();
            Backups.Clear();
            foreach (var backup in backups)
            {
                Backups.Add(new BackupSummary(
                    backup.Id,
                    backup.CreatedUtc,
                    backup.TargetDirectory,
                    backup.ProfileName,
                    backup.FileCount,
                    backup.IsRestored));
            }

            SelectedBackup = Backups.FirstOrDefault(backup => backup.Id == selectedId);
        }
    }

    public override void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DisposeStagedOperation();
        base.Dispose();
    }
}
