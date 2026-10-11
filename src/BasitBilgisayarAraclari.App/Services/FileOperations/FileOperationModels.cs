using System.IO;
using BasitBilgisayarAraclari.App.Localization;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

public enum FileOperationKind
{
    ExtractAndMerge,
    GameModInstall
}

public enum ConflictPolicy
{
    SkipConflicts,
    OverwriteConflicts
}

public sealed record FilePreviewEntry(
    string RelativePath,
    string Sources,
    long SizeBytes,
    bool HasSourceConflict,
    bool HasTargetConflict,
    bool MatchesTarget)
{
    public string ConflictDescription => LocalizationManager.Instance.Translate(
        HasSourceConflict && HasTargetConflict
            ? "Kaynak ve hedef çakışması"
            : HasSourceConflict
                ? "Kaynaklar farklı"
                : HasTargetConflict
                    ? "Hedefte farklı dosya"
                    : MatchesTarget
                        ? "Hedefle aynı"
                        : "Çakışma yok");
}

public sealed record FileOperationResult(
    int AppliedFiles,
    int SkippedFiles,
    int BackedUpFiles,
    Guid? BackupId);

public sealed record BackupSummary(
    Guid Id,
    DateTime CreatedUtc,
    string TargetDirectory,
    string ProfileName,
    int FileCount,
    bool IsRestored)
{
    public string Label => LocalizationManager.Instance.Format(
        "{created:g} · {profile} · {count:N0} dosya",
        new Dictionary<string, object?>
        {
            ["created"] = CreatedUtc.ToLocalTime(),
            ["profile"] = ProfileName,
            ["count"] = FileCount
        });
}

public sealed class ArchivePasswordRequiredException(
    string archivePath,
    bool suppliedPasswordWasRejected,
    Exception? innerException = null)
    : Exception(
        suppliedPasswordWasRejected
            ? "Arşiv parolası kabul edilmedi."
            : "Bu arşiv bir parola gerektiriyor.",
        innerException)
{
    public string ArchivePath { get; } = archivePath;

    public bool SuppliedPasswordWasRejected { get; } = suppliedPasswordWasRejected;
}

public sealed class FileOperationRollbackException(
    Guid? recoveryBackupId,
    IReadOnlyList<string> rollbackIssues,
    Exception innerException,
    string? retainedRecoveryDirectory = null)
    : IOException(BuildMessage(recoveryBackupId, rollbackIssues, retainedRecoveryDirectory), innerException)
{
    private const string LocalizedMessageTemplate =
        "İşlem hata verdi ve {0} dosya otomatik geri alınamadı.{1} {2}";

    public Guid? RecoveryBackupId { get; } = recoveryBackupId;

    public IReadOnlyList<string> RollbackIssues { get; } = rollbackIssues;

    public string? RetainedRecoveryDirectory { get; } = retainedRecoveryDirectory;

    public string GetLocalizedMessage()
    {
        var localization = LocalizationManager.Instance;
        var backupText = localization.TranslateUserMessage(
            BuildRecoveryText(RecoveryBackupId, RetainedRecoveryDirectory));
        var issueText = string.Join("; ", RollbackIssues.Take(3)
            .Select(issue => localization.TranslateUserMessage(issue)));

        return localization.Format(LocalizedMessageTemplate, new Dictionary<string, object?>
        {
            ["0"] = RollbackIssues.Count,
            ["1"] = backupText,
            ["2"] = issueText
        });
    }

    private static string BuildMessage(Guid? backupId, IReadOnlyList<string> issues, string? retainedRecoveryDirectory)
    {
        return $"İşlem hata verdi ve {issues.Count} dosya otomatik geri alınamadı.{BuildRecoveryText(backupId, retainedRecoveryDirectory)}¤{string.Join("; ", issues.Take(3))}";
    }

    private static string BuildRecoveryText(Guid? backupId, string? retainedRecoveryDirectory)
    {
        return backupId.HasValue
            ? $" Kurtarma yedeği ID: {backupId.Value}. Oyun Modu Yerleştir aracındaki yedek listesinden geri yükleyebilirsin."
            : retainedRecoveryDirectory is not null
                ? $" Kurtarma kaydı oluşturulamadı; özgün dosya kopyaları silinmeden şu klasörde bırakıldı: {retainedRecoveryDirectory}"
                : " Kurtarma yedeği oluşturulamadı; hedefi ve uygulama erişimini kontrol et.";
    }
}

internal sealed record StagedFile(
    string RelativePath,
    string SourceLabel,
    string StagedPath,
    string Sha256,
    long SizeBytes,
    int Order);

internal sealed record TargetFileSnapshot(bool Exists, string? Sha256);

public sealed class StagedFileOperation : IDisposable
{
    private readonly string _stagingDirectory;
    private bool _disposed;
    private bool _preserveStagingForRecovery;

    internal StagedFileOperation(
        string stagingDirectory,
        string targetDirectory,
        FileOperationKind kind,
        IReadOnlyList<StagedFile> files,
        IReadOnlyList<FilePreviewEntry> entries,
        IReadOnlyDictionary<string, TargetFileSnapshot> targetSnapshots)
    {
        _stagingDirectory = stagingDirectory;
        TargetDirectory = targetDirectory;
        Kind = kind;
        Files = files;
        Entries = entries;
        TargetSnapshots = targetSnapshots;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    public string TargetDirectory { get; }

    public FileOperationKind Kind { get; }

    public IReadOnlyList<FilePreviewEntry> Entries { get; }

    internal IReadOnlyList<StagedFile> Files { get; }

    internal IReadOnlyDictionary<string, TargetFileSnapshot> TargetSnapshots { get; }

    internal string StagingDirectory => _stagingDirectory;

    internal bool IsDisposed => _disposed;

    internal void PreserveStagingForRecovery() => _preserveStagingForRecovery = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_preserveStagingForRecovery)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_stagingDirectory))
            {
                Directory.Delete(_stagingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary cleanup can be retried by the next application launch.
        }
        catch (UnauthorizedAccessException)
        {
            // Never elevate just to remove the application's temporary files.
        }
    }
}

internal sealed record BackupManifest(
    Guid Id,
    DateTime CreatedUtc,
    string TargetDirectory,
    string ProfileName,
    bool IsRestored,
    IReadOnlyList<BackupFile> Files,
    IReadOnlyList<string>? RestoredPaths = null);

internal sealed record BackupFile(
    string RelativePath,
    string? BackupPayloadName,
    string InstalledSha256,
    bool ExistedBefore,
    string? OriginalSha256);
