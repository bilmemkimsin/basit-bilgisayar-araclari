namespace BasitBilgisayarAraclari.App.Services.FileOperations;

public interface IFileOperationService
{
    Task<StagedFileOperation> PreviewAsync(
        FileOperationKind kind,
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        bool skipSingleTopFolder,
        IReadOnlyDictionary<string, string>? archivePasswords = null,
        CancellationToken cancellationToken = default);

    Task<FileOperationResult> ApplyAsync(
        StagedFileOperation operation,
        ConflictPolicy conflictPolicy,
        string? profileName = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<BackupSummary> GetBackups();

    Task<RestoreResult> RestoreBackupAsync(Guid backupId, CancellationToken cancellationToken = default);
}

public sealed record RestoreResult(int RestoredFiles, int SkippedChangedFiles, bool IsFullyRestored);

public sealed record FileOperationLimits(
    int MaximumEntries = 100_000,
    long MaximumFileBytes = 4L * 1024 * 1024 * 1024,
    long MaximumTotalBytes = 12L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumEntries is <= 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        }

        if (MaximumFileBytes <= 0 || MaximumTotalBytes < MaximumFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }
    }
}
