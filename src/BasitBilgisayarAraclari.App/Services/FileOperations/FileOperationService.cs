using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Archives.GZip;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Common.SevenZip;
using SharpCompress.Common.Tar;
using SharpCompress.Readers;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

public sealed class FileOperationService : IFileOperationService
{
    private const int CopyBufferSize = 128 * 1024;
    private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? TarFilePartField = typeof(TarEntry).GetField("_filePart", NonPublicInstance);
    private static readonly PropertyInfo? TarHeaderProperty = TarFilePartField?.FieldType.GetProperty("Header", NonPublicInstance);
    private static readonly PropertyInfo? TarEntryTypeProperty = TarHeaderProperty?.PropertyType.GetProperty("EntryType", NonPublicInstance);
    private readonly string _stagingRoot;
    private readonly string _backupRoot;
    private readonly FileOperationLimits _limits;
    private readonly Func<string, Process>? _workerLauncher;
    private readonly TimeSpan? _workerTimeout;
    private readonly long? _workerMemoryLimitBytes;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record AppliedFileChange(
        string Destination,
        string RelativePath,
        string InstalledSha256,
        bool ExistedBefore,
        string? OriginalPayloadPath,
        string? OriginalSha256);

    public FileOperationService(
        string? applicationDataRoot = null,
        FileOperationLimits? limits = null)
        : this(applicationDataRoot, limits, stagingRoot: null, workerLauncher: null)
    {
    }

    internal FileOperationService(
        string? applicationDataRoot,
        FileOperationLimits? limits,
        string? stagingRoot,
        Func<string, Process>? workerLauncher,
        TimeSpan? workerTimeout = null,
        long? workerMemoryLimitBytes = null)
    {
        var dataRoot = applicationDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BasitBilgisayarAraclari");
        _stagingRoot = stagingRoot is null
            ? Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari", "staging")
            : Path.GetFullPath(stagingRoot);
        _backupRoot = Path.Combine(dataRoot, "backups");
        _limits = limits ?? new FileOperationLimits();
        _limits.Validate();
        _workerLauncher = workerLauncher;
        _workerTimeout = workerTimeout;
        _workerMemoryLimitBytes = workerMemoryLimitBytes;
    }

    public Task<StagedFileOperation> PreviewAsync(
        FileOperationKind kind,
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        bool skipSingleTopFolder,
        IReadOnlyDictionary<string, string>? archivePasswords = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Preview(kind, sourcePaths, targetDirectory, skipSingleTopFolder, archivePasswords, cancellationToken),
            cancellationToken);

    public Task<FileOperationResult> ApplyAsync(
        StagedFileOperation operation,
        ConflictPolicy conflictPolicy,
        string? profileName = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Apply(operation, conflictPolicy, profileName, cancellationToken), cancellationToken);

    public IReadOnlyList<BackupSummary> GetBackups()
    {
        if (!Directory.Exists(_backupRoot))
        {
            return [];
        }

        try
        {
            RejectReparseComponents(_backupRoot, allowMissingTail: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }

        var summaries = new List<BackupSummary>();
        foreach (var backupDirectory in Directory.EnumerateDirectories(_backupRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var manifestPath = Path.Combine(backupDirectory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                if (HasReparsePointBetween(_backupRoot, backupDirectory))
                {
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), _jsonOptions);
                if (manifest is null || !Guid.TryParse(Path.GetFileName(backupDirectory), out var folderId) || folderId != manifest.Id)
                {
                    continue;
                }

                summaries.Add(new BackupSummary(
                    manifest.Id,
                    manifest.CreatedUtc,
                    manifest.TargetDirectory,
                    manifest.ProfileName,
                    manifest.Files.Count,
                    manifest.IsRestored));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore an incomplete or unreadable local backup record.
            }
        }

        return summaries.OrderByDescending(summary => summary.CreatedUtc).ToArray();
    }

    public Task<RestoreResult> RestoreBackupAsync(Guid backupId, CancellationToken cancellationToken = default) =>
        Task.Run(() => RestoreBackup(backupId, cancellationToken), cancellationToken);

    private StagedFileOperation Preview(
        FileOperationKind kind,
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        bool skipSingleTopFolder,
        IReadOnlyDictionary<string, string>? archivePasswords,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("En az bir arşiv veya klasör seç.", nameof(sourcePaths));
        }

        var normalizedSources = sourcePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalizedTarget = Path.GetFullPath(targetDirectory);
        foreach (var source in normalizedSources)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                throw new FileNotFoundException("Kaynak dosya veya klasör bulunamadı.", source);
            }

            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Bağlantı veya junction kaynak olarak kullanılamaz: {source}");
            }

            if (Directory.Exists(source) &&
                (IsSameOrChildPath(normalizedTarget, source) || IsSameOrChildPath(source, normalizedTarget)))
            {
                throw new InvalidOperationException("Hedef klasör kaynak klasörün içinde olamaz.");
            }
        }

        RejectReparseComponents(normalizedTarget, allowMissingTail: true);
        RejectReparseComponents(_stagingRoot, allowMissingTail: true);
        Directory.CreateDirectory(_stagingRoot);
        RejectReparseComponents(_stagingRoot, allowMissingTail: false);
        var stagingDirectory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        var stagedFiles = new List<StagedFile>();
        var totalBytes = 0L;
        var order = 0;
        var entryCount = 0;
        try
        {
            foreach (var source in normalizedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFiles = Directory.Exists(source)
                    ? StageFolder(source, source, stagingDirectory, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)), ref totalBytes, ref order, ref entryCount, cancellationToken)
                    : StageArchive(source, stagingDirectory, archivePasswords, ref totalBytes, ref order, ref entryCount, cancellationToken);

                if (skipSingleTopFolder)
                {
                    StripSingleTopFolder(sourceFiles);
                }

                stagedFiles.AddRange(sourceFiles);
                if (stagedFiles.Count > _limits.MaximumEntries)
                {
                    throw new InvalidDataException($"Kaynaklar { _limits.MaximumEntries:N0} dosya sınırını aşıyor.");
                }
            }

            var previewEntries = BuildPreviewEntries(stagedFiles, normalizedTarget, cancellationToken, out var targetSnapshots);
            return new StagedFileOperation(stagingDirectory, normalizedTarget, kind, stagedFiles, previewEntries, targetSnapshots);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private List<StagedFile> StageFolder(
        string sourceRoot,
        string currentDirectory,
        string stagingDirectory,
        string sourceLabel,
        ref long totalBytes,
        ref int order,
        ref int entryCount,
        CancellationToken cancellationToken)
    {
        var rootAttributes = File.GetAttributes(sourceRoot);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Kısayol veya bağlantı klasörü kaynak olamaz: {sourceRoot}");
        }

        var result = new List<StagedFile>();
        var pending = new Stack<string>();
        pending.Push(currentDirectory);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Kaynakta bağlantı girdisi bulundu: {directory}");
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CountEntry(ref entryCount);
                var childAttributes = File.GetAttributes(child);
                if ((childAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Kaynakta bağlantı girdisi bulundu: {child}");
                }

                if ((childAttributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                    continue;
                }

                var relative = Path.GetRelativePath(sourceRoot, child);
                var safeRelative = ValidateRelativePath(relative);
                var file = new FileInfo(child);
                if (file.Length > _limits.MaximumFileBytes)
                {
                    throw new InvalidDataException($"Dosya boyut sınırını aşıyor: {safeRelative}");
                }

                var staged = CopyToStage(file.OpenRead(), safeRelative, sourceLabel, stagingDirectory, ref totalBytes, ref order, cancellationToken, file.Length);
                result.Add(staged);
            }
        }

        return result;
    }

    private List<StagedFile> StageArchive(
        string archivePath,
        string stagingDirectory,
        IReadOnlyDictionary<string, string>? archivePasswords,
        ref long totalBytes,
        ref int order,
        ref int entryCount,
        CancellationToken cancellationToken)
    {
        var format = GetSupportedFormat(archivePath);
        var sourceLabel = Path.GetFileName(archivePath);
        var password = archivePasswords?.GetValueOrDefault(archivePath);
        var result = new List<StagedFile>();
        try
        {
            if (format == "gz")
            {
                var validationBytes = 0L;
                ValidateGzipChecksumAndLimits(archivePath, ref validationBytes, cancellationToken);
            }

            if (format == "7z")
            {
                result.AddRange(SevenZipWorkerClient.Extract(
                    archivePath,
                    password,
                    stagingDirectory,
                    sourceLabel,
                    _limits,
                    ref totalBytes,
                    ref order,
                    ref entryCount,
                    cancellationToken,
                    _workerLauncher,
                    _workerTimeout,
                    _workerMemoryLimitBytes));
            }
            else
            {
                if (format == "zip")
                {
                    using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(
                        archivePath,
                        new ReaderOptions { Password = password });
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CountEntry(ref entryCount);
                        if (IsLinkOrSpecialEntry(entry, format))
                        {
                            throw new InvalidDataException($"ZIP bağlantı veya özel dosya girdisi reddedildi: {entry.Key}");
                        }

                        if (entry.IsDirectory)
                        {
                            var directoryPath = (entry.Key ?? string.Empty).TrimEnd('/', '\\');
                            if (directoryPath.Length > 0)
                            {
                                _ = ValidateRelativePath(directoryPath);
                            }

                            continue;
                        }

                        var relative = ValidateArchiveEntryPath(entry.Key, archivePath, format);
                        if (entry.IsEncrypted && string.IsNullOrEmpty(password))
                        {
                            throw new ArchivePasswordRequiredException(archivePath, suppliedPasswordWasRejected: false);
                        }

                        if (entry.Size is > 0 && entry.Size > _limits.MaximumFileBytes)
                        {
                            throw new InvalidDataException($"Arşiv girdisi boyut sınırını aşıyor: {relative}");
                        }

                        result.Add(CopyArchiveEntryToStage(
                            output => entry.WriteTo(output, new ExtractionOptions { CheckCrc = true }),
                            relative,
                            sourceLabel,
                            stagingDirectory,
                            ref totalBytes,
                            ref order,
                            cancellationToken,
                            entry.Size));
                    }
                }
                else
                {
                    using var stream = File.OpenRead(archivePath);
                    using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions { Password = password });
                    while (reader.MoveToNextEntry())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        CountEntry(ref entryCount);
                        var entry = reader.Entry;
                        if (entry is null)
                        {
                            throw new InvalidDataException("Arşiv girdisi okunamadı.");
                        }

                        if (IsLinkOrSpecialEntry(entry, format))
                        {
                            throw new InvalidDataException($"Arşiv bağlantı veya özel dosya girdisi içeriyor: {entry.Key}");
                        }

                        if (entry.IsDirectory)
                        {
                            var directoryPath = (entry.Key ?? string.Empty).TrimEnd('/', '\\');
                            if (directoryPath.Length > 0)
                            {
                                _ = ValidateRelativePath(directoryPath);
                            }

                            continue;
                        }

                        var relative = ValidateArchiveEntryPath(entry.Key, archivePath, format);
                        if (entry.IsEncrypted && string.IsNullOrEmpty(password))
                        {
                            throw new ArchivePasswordRequiredException(archivePath, suppliedPasswordWasRejected: false);
                        }

                        if (entry.Size is > 0 && entry.Size > _limits.MaximumFileBytes)
                        {
                            throw new InvalidDataException($"Arşiv girdisi boyut sınırını aşıyor: {relative}");
                        }

                        result.Add(CopyToStage(reader.OpenEntryStream(), relative, sourceLabel, stagingDirectory, ref totalBytes, ref order, cancellationToken, entry.Size, reader.Cancel));
                        if (result.Count > _limits.MaximumEntries)
                        {
                            throw new InvalidDataException($"Arşiv { _limits.MaximumEntries:N0} dosya sınırını aşıyor.");
                        }
                    }
                }
            }
        }
        catch (ArchivePasswordRequiredException)
        {
            throw;
        }
        catch (Exception exception) when (IsPasswordException(exception))
        {
            throw new ArchivePasswordRequiredException(archivePath, suppliedPasswordWasRejected: !string.IsNullOrEmpty(password), exception);
        }
        catch (SharpCompressException exception)
        {
            throw new InvalidDataException($"'{sourceLabel}' arşivi okunamadı veya bozuk.", exception);
        }
        catch (Exception exception) when ((exception is NotSupportedException or InvalidOperationException) && (format is "gz" or "bz2" or "xz"))
        {
            throw new NotSupportedException($"'{Path.GetExtension(archivePath)}' girdisi çıkarılamadı. Bu biçimler tek dosya akışı veya TAR arşivi olarak desteklenir.", exception);
        }

        return result;
    }

    private StagedFile CopyArchiveEntryToStage(
        Action<Stream> writeEntry,
        string relativePath,
        string sourceLabel,
        string stagingDirectory,
        ref long totalBytes,
        ref int order,
        CancellationToken cancellationToken,
        long? expectedSize)
    {
        var stagePath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".payload");
        try
        {
            using var output = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var boundedOutput = new QuotaHashingWriteStream(output, hash, totalBytes, _limits, cancellationToken);
            writeEntry(boundedOutput);
            boundedOutput.Flush();

            if (expectedSize is >= 0 && boundedOutput.BytesWritten != expectedSize.Value)
            {
                throw new InvalidDataException($"Arşiv girdisinin bildirilen boyutu ile okunan boyutu uyuşmuyor: {relativePath}");
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset());
            totalBytes += boundedOutput.BytesWritten;
            return new StagedFile(relativePath, sourceLabel, stagePath, digest, boundedOutput.BytesWritten, order++);
        }
        catch
        {
            TryDeleteFile(stagePath);
            throw;
        }
    }

    private void ValidateGzipChecksumAndLimits(string archivePath, ref long totalBytes, CancellationToken cancellationToken)
    {
        using var archive = GZipArchive.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Size is > 0 && entry.Size > _limits.MaximumFileBytes)
            {
                throw new InvalidDataException("GZ girdisi dosya boyutu sınırını aşıyor.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var boundedOutput = new QuotaHashingWriteStream(Stream.Null, hash, totalBytes, _limits, cancellationToken);
            entry.WriteTo(boundedOutput, new ExtractionOptions { CheckCrc = true });
            boundedOutput.Flush();
            if (entry.Size is >= 0 && boundedOutput.BytesWritten != entry.Size)
            {
                throw new InvalidDataException("GZ girdisinin kayıtlı boyutu ile açılan veri uyuşmuyor.");
            }

            totalBytes += boundedOutput.BytesWritten;
        }
    }

    private StagedFile CopyToStage(
        Stream input,
        string relativePath,
        string sourceLabel,
        string stagingDirectory,
        ref long totalBytes,
        ref int order,
        CancellationToken cancellationToken,
        long? expectedSize,
        Action? cancelArchiveReader = null)
    {
        using (input)
        {
            try
            {
            var stagePath = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".payload");
            using var output = new FileStream(stagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBufferSize];
            var fileBytes = 0L;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileBytes += read;
                totalBytes += read;
                if (fileBytes > _limits.MaximumFileBytes || totalBytes > _limits.MaximumTotalBytes)
                {
                    output.Close();
                    TryDeleteFile(stagePath);
                    throw new InvalidDataException("Çıkarılan veri güvenlik boyut sınırını aşıyor.");
                }

                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }

            if (expectedSize is >= 0 && fileBytes != expectedSize.Value)
            {
                throw new InvalidDataException($"Arşiv girdisinin bildirilen boyutu ile okunan boyutu uyuşmuyor: {relativePath}");
            }

            var digest = Convert.ToHexString(hash.GetHashAndReset());
            return new StagedFile(relativePath, sourceLabel, stagePath, digest, fileBytes, order++);
            }
            catch
            {
                try
                {
                    cancelArchiveReader?.Invoke();
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    // The original copy or cancellation error is more useful.
                }

                throw;
            }
        }
    }

    private IReadOnlyList<FilePreviewEntry> BuildPreviewEntries(
        IReadOnlyList<StagedFile> stagedFiles,
        string targetDirectory,
        CancellationToken cancellationToken,
        out IReadOnlyDictionary<string, TargetFileSnapshot> targetSnapshots)
    {
        var entries = new List<FilePreviewEntry>();
        var snapshots = new Dictionary<string, TargetFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in stagedFiles.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = group.OrderBy(file => file.Order).ToArray();
            var uniqueHashes = files.Select(file => file.Sha256).Distinct(StringComparer.Ordinal).ToArray();
            var targetPath = CombineSafePath(targetDirectory, files[^1].RelativePath);
            RejectReparseComponents(Path.GetDirectoryName(targetPath)!, allowMissingTail: true);
            RejectReparseFile(targetPath);
            var targetExists = File.Exists(targetPath);
            var targetHash = targetExists ? ComputeSha256(targetPath) : null;
            snapshots[group.Key] = new TargetFileSnapshot(targetExists, targetHash);
            var matchesTarget = targetHash is not null && uniqueHashes.Contains(targetHash, StringComparer.Ordinal);
            var chosen = files[^1];
            if (targetHash is not null)
            {
                chosen = files.LastOrDefault(file => file.Sha256 == targetHash) ?? chosen;
            }

            entries.Add(new FilePreviewEntry(
                chosen.RelativePath,
                string.Join(", ", files.Select(file => file.SourceLabel).Distinct(StringComparer.OrdinalIgnoreCase)),
                chosen.SizeBytes,
                uniqueHashes.Length > 1,
                targetExists && !matchesTarget,
                matchesTarget));
        }

        targetSnapshots = snapshots;
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private FileOperationResult Apply(
        StagedFileOperation operation,
        ConflictPolicy conflictPolicy,
        string? profileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.IsDisposed || !Directory.Exists(operation.StagingDirectory))
        {
            throw new ObjectDisposedException(nameof(StagedFileOperation), "Önizleme verisi artık kullanılamıyor.");
        }

        RejectReparseComponents(operation.TargetDirectory, allowMissingTail: true);
        Directory.CreateDirectory(operation.TargetDirectory);
        RejectReparseComponents(operation.TargetDirectory, allowMissingTail: false);

        var grouped = operation.Files
            .GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var decisions = new List<(string Relative, StagedFile Chosen, string Destination, bool Exists, string? ExistingHash)>();
        var skipped = 0;
        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = group.OrderBy(file => file.Order).ToArray();
            var uniqueHashes = candidates.Select(file => file.Sha256).Distinct(StringComparer.Ordinal).ToArray();
            var destination = CombineSafePath(operation.TargetDirectory, candidates[^1].RelativePath);
            RejectReparseComponents(Path.GetDirectoryName(destination)!, allowMissingTail: true);
            RejectReparseFile(destination);
            if (Directory.Exists(destination))
            {
                throw new IOException($"Hedefte aynı yolda bir klasör var: {candidates[^1].RelativePath}");
            }

            var snapshot = operation.TargetSnapshots[group.Key];
            var exists = snapshot.Exists;
            var existingHash = snapshot.Sha256;
            var existsNow = File.Exists(destination);
            var currentHash = existsNow ? ComputeSha256(destination) : null;
            if (existsNow != exists || currentHash != existingHash)
            {
                throw new IOException($"Hedef önizlemeden sonra değişti. Önizlemeyi yenile: {candidates[^1].RelativePath}");
            }

            var sourceConflict = uniqueHashes.Length > 1;
            var targetConflict = exists && !uniqueHashes.Contains(existingHash!, StringComparer.Ordinal);
            var hasConflict = sourceConflict || targetConflict;
            if (hasConflict && conflictPolicy == ConflictPolicy.SkipConflicts)
            {
                skipped++;
                continue;
            }

            if (!hasConflict && exists)
            {
                skipped++;
                continue;
            }

            var chosen = candidates[^1];
            decisions.Add((chosen.RelativePath, chosen, destination, exists, existingHash));
        }

        var backupId = operation.Kind == FileOperationKind.GameModInstall && decisions.Count > 0
            ? Guid.NewGuid()
            : (Guid?)null;
        var backupDirectory = backupId.HasValue ? Path.Combine(_backupRoot, backupId.Value.ToString("N")) : null;
        var backupFiles = new List<BackupFile>();
        var applied = new List<AppliedFileChange>();
        try
        {
            if (backupDirectory is not null)
            {
                RejectReparseComponents(_backupRoot, allowMissingTail: true);
                Directory.CreateDirectory(_backupRoot);
                RejectReparseComponents(_backupRoot, allowMissingTail: false);
                Directory.CreateDirectory(backupDirectory);
                RejectReparseComponents(backupDirectory, allowMissingTail: false);
            }

            foreach (var decision in decisions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparseComponents(Path.GetDirectoryName(decision.Destination)!, allowMissingTail: true);
                Directory.CreateDirectory(Path.GetDirectoryName(decision.Destination)!);
                RejectReparseComponents(Path.GetDirectoryName(decision.Destination)!, allowMissingTail: false);
                RejectReparseFile(decision.Destination);
                var existsNow = File.Exists(decision.Destination);
                var currentHash = existsNow ? ComputeSha256(decision.Destination) : null;
                if (existsNow != decision.Exists || currentHash != decision.ExistingHash)
                {
                    throw new IOException($"Hedef önizlemeden sonra değişti. Güvenlik için işlem durduruldu: {decision.Relative}");
                }

                string? backupPayload = null;
                string? rollbackPayloadPath = null;
                if (operation.Kind == FileOperationKind.GameModInstall)
                {
                    backupPayload = existsNow ? Guid.NewGuid().ToString("N") + ".backup" : null;
                    backupFiles.Add(new BackupFile(decision.Relative, backupPayload, decision.Chosen.Sha256, existsNow, decision.ExistingHash));
                    if (existsNow && backupPayload is not null)
                    {
                        File.Copy(decision.Destination, Path.Combine(backupDirectory!, backupPayload), overwrite: false);
                        rollbackPayloadPath = Path.Combine(backupDirectory!, backupPayload);
                    }
                }

                if (existsNow && rollbackPayloadPath is null)
                {
                    rollbackPayloadPath = Path.Combine(operation.StagingDirectory, Guid.NewGuid().ToString("N") + ".rollback");
                    File.Copy(decision.Destination, rollbackPayloadPath, overwrite: false);
                }

                var temporaryPath = Path.Combine(Path.GetDirectoryName(decision.Destination)!, ".bbat-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(decision.Chosen.StagedPath, temporaryPath, overwrite: false);
                    File.Move(temporaryPath, decision.Destination, overwrite: existsNow);
                }
                finally
                {
                    TryDeleteFile(temporaryPath);
                }

                applied.Add(new AppliedFileChange(
                    decision.Destination,
                    decision.Relative,
                    decision.Chosen.Sha256,
                    existsNow,
                    rollbackPayloadPath,
                    decision.ExistingHash));
            }

            if (backupId is not null && backupDirectory is not null)
            {
                var manifest = new BackupManifest(
                    backupId.Value,
                    DateTime.UtcNow,
                    operation.TargetDirectory,
                    profileName ?? string.Empty,
                    false,
                    backupFiles,
                    []);
                PersistManifest(backupDirectory, manifest);
            }

            return new FileOperationResult(applied.Count, skipped, backupFiles.Count(file => file.ExistedBefore), backupId);
        }
        catch (Exception operationException)
        {
            var rollbackIssues = RollBackApplied(applied);
            if (rollbackIssues.Count == 0)
            {
                if (backupDirectory is not null)
                {
                    TryDeleteDirectory(backupDirectory);
                }

                throw;
            }

            Guid? recoveryBackupId = backupId;
            string? retainedRecoveryDirectory = null;
            try
            {
                if (backupDirectory is not null && backupId is not null)
                {
                    PersistManifest(backupDirectory, new BackupManifest(
                        backupId.Value,
                        DateTime.UtcNow,
                        operation.TargetDirectory,
                        profileName ?? string.Empty,
                        false,
                        backupFiles,
                        []));
                }
                else
                {
                    recoveryBackupId = PersistEmergencyRecovery(operation.TargetDirectory, applied);
                }
            }
            catch (Exception persistenceException) when (persistenceException is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                recoveryBackupId = null;
                if (backupDirectory is not null)
                {
                    retainedRecoveryDirectory = backupDirectory;
                }
                else
                {
                    operation.PreserveStagingForRecovery();
                    retainedRecoveryDirectory = operation.StagingDirectory;
                }

                rollbackIssues = rollbackIssues.Append($"Kurtarma yedeği yazılamadı: {persistenceException.Message}").ToArray();
            }

            throw new FileOperationRollbackException(recoveryBackupId, rollbackIssues, operationException, retainedRecoveryDirectory);
        }
    }

    private RestoreResult RestoreBackup(Guid backupId, CancellationToken cancellationToken)
    {
        var backupDirectory = Path.Combine(_backupRoot, backupId.ToString("N"));
        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        if (!File.Exists(manifestPath) || HasReparsePointBetween(_backupRoot, backupDirectory))
        {
            throw new FileNotFoundException("Yedek kaydı bulunamadı.", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), _jsonOptions)
            ?? throw new InvalidDataException("Yedek kaydı okunamadı.");
        if (manifest.Id != backupId || manifest.IsRestored)
        {
            return new RestoreResult(0, 0, manifest.IsRestored);
        }

        var restored = 0;
        var skipped = 0;
        var restoredPaths = new HashSet<string>(manifest.RestoredPaths ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var backup in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ValidateRelativePath(backup.RelativePath);
            if (restoredPaths.Contains(relative))
            {
                continue;
            }

            var destination = CombineSafePath(manifest.TargetDirectory, relative);
            RejectReparseComponents(Path.GetDirectoryName(destination)!, allowMissingTail: true);
            RejectReparseFile(destination);
            if (backup.ExistedBefore && File.Exists(destination) && backup.OriginalSha256 is not null && ComputeSha256(destination) == backup.OriginalSha256)
            {
                restoredPaths.Add(relative);
                continue;
            }

            if (!backup.ExistedBefore && !File.Exists(destination))
            {
                restoredPaths.Add(relative);
                continue;
            }

            if (!File.Exists(destination) || ComputeSha256(destination) != backup.InstalledSha256)
            {
                skipped++;
                continue;
            }

            if (backup.ExistedBefore)
            {
                if (string.IsNullOrWhiteSpace(backup.BackupPayloadName) || Path.GetFileName(backup.BackupPayloadName) != backup.BackupPayloadName)
                {
                    throw new InvalidDataException("Yedek girdisi geçersiz.");
                }

                var payloadPath = Path.Combine(backupDirectory, backup.BackupPayloadName);
                if (!File.Exists(payloadPath) || HasReparsePointBetween(backupDirectory, payloadPath))
                {
                    throw new InvalidDataException("Yedek dosyası bulunamadı veya güvenli değil.");
                }

                if (backup.OriginalSha256 is null || ComputeSha256(payloadPath) != backup.OriginalSha256)
                {
                    throw new InvalidDataException("Yedek dosyasının sağlama toplamı doğrulanamadı; hedef dosya korunuyor.");
                }

                var temp = Path.Combine(Path.GetDirectoryName(destination)!, ".bbat-" + Guid.NewGuid().ToString("N") + ".tmp");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    File.Copy(payloadPath, temp, overwrite: false);
                    if (ComputeSha256(temp) != backup.OriginalSha256)
                    {
                        throw new InvalidDataException("Geçici geri yükleme kopyasının sağlama toplamı hatalı; hedef dosya korunuyor.");
                    }

                    RejectReparseComponents(Path.GetDirectoryName(destination)!, allowMissingTail: false);
                    RejectReparseFile(destination);
                    if (!File.Exists(destination) || ComputeSha256(destination) != backup.InstalledSha256)
                    {
                        skipped++;
                        continue;
                    }

                    File.Move(temp, destination, overwrite: true);
                }
                finally
                {
                    TryDeleteFile(temp);
                }
            }
            else
            {
                RejectReparseComponents(Path.GetDirectoryName(destination)!, allowMissingTail: false);
                RejectReparseFile(destination);
                if (!File.Exists(destination) || ComputeSha256(destination) != backup.InstalledSha256)
                {
                    skipped++;
                    continue;
                }

                File.Delete(destination);
            }

            restoredPaths.Add(relative);
            restored++;
        }

        var isFullyRestored = restoredPaths.Count >= manifest.Files.Count;
        var updated = manifest with { IsRestored = isFullyRestored, RestoredPaths = restoredPaths.ToArray() };
        var temporaryManifest = manifestPath + ".tmp";
        File.WriteAllText(temporaryManifest, JsonSerializer.Serialize(updated, _jsonOptions));
        File.Move(temporaryManifest, manifestPath, overwrite: true);
        return new RestoreResult(restored, skipped, isFullyRestored);
    }

    private IReadOnlyList<string> RollBackApplied(IReadOnlyList<AppliedFileChange> applied)
    {
        var issues = new List<string>();
        foreach (var item in applied.Reverse())
        {
            try
            {
                RejectReparseComponents(Path.GetDirectoryName(item.Destination)!, allowMissingTail: false);
                if (File.Exists(item.Destination) && (File.GetAttributes(item.Destination) & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add($"Bağlantı haline gelen hedef geri alınmadı: {item.RelativePath}");
                    continue;
                }

                var exists = File.Exists(item.Destination);
                var currentHash = exists ? ComputeSha256(item.Destination) : null;
                if (item.ExistedBefore && currentHash == item.OriginalSha256 || !item.ExistedBefore && !exists)
                {
                    continue;
                }

                if (!exists || currentHash != item.InstalledSha256)
                {
                    issues.Add($"İşlem sonrası değişen dosyaya dokunulmadı: {item.RelativePath}");
                    continue;
                }

                if (item.ExistedBefore)
                {
                    if (item.OriginalPayloadPath is null || !File.Exists(item.OriginalPayloadPath))
                    {
                        issues.Add($"Özgün dosyanın geçici kopyası bulunamadı: {item.RelativePath}");
                        continue;
                    }

                    var temporaryPath = Path.Combine(Path.GetDirectoryName(item.Destination)!, ".bbat-" + Guid.NewGuid().ToString("N") + ".rollback");
                    try
                    {
                        File.Copy(item.OriginalPayloadPath, temporaryPath, overwrite: false);
                        File.Move(temporaryPath, item.Destination, overwrite: true);
                    }
                    finally
                    {
                        TryDeleteFile(temporaryPath);
                    }
                }
                else
                {
                    File.Delete(item.Destination);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                issues.Add($"{item.RelativePath}: {exception.Message}");
            }
        }

        return issues;
    }

    private Guid PersistEmergencyRecovery(string targetDirectory, IReadOnlyList<AppliedFileChange> applied)
    {
        RejectReparseComponents(_backupRoot, allowMissingTail: true);
        Directory.CreateDirectory(_backupRoot);
        RejectReparseComponents(_backupRoot, allowMissingTail: false);
        var id = Guid.NewGuid();
        var directory = Path.Combine(_backupRoot, id.ToString("N"));
        Directory.CreateDirectory(directory);
        var files = new List<BackupFile>();
        try
        {
            foreach (var item in applied)
            {
                var payloadName = item.ExistedBefore ? Guid.NewGuid().ToString("N") + ".backup" : null;
                if (item.ExistedBefore)
                {
                    if (item.OriginalPayloadPath is null || !File.Exists(item.OriginalPayloadPath))
                    {
                        throw new FileNotFoundException("Kurtarma için özgün dosya bulunamadı.", item.OriginalPayloadPath);
                    }

                    File.Copy(item.OriginalPayloadPath, Path.Combine(directory, payloadName!), overwrite: false);
                }

                files.Add(new BackupFile(item.RelativePath, payloadName, item.InstalledSha256, item.ExistedBefore, item.OriginalSha256));
            }

            PersistManifest(directory, new BackupManifest(
                id,
                DateTime.UtcNow,
                targetDirectory,
                "Genel işlem kurtarma",
                false,
                files,
                []));
            return id;
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private void PersistManifest(string directory, BackupManifest manifest)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var temporaryManifestPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryManifestPath, JsonSerializer.Serialize(manifest, _jsonOptions));
        File.Move(temporaryManifestPath, manifestPath, overwrite: File.Exists(manifestPath));
    }

    private static void StripSingleTopFolder(List<StagedFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        var firstSegments = files.Select(file => file.RelativePath.Split(Path.DirectorySeparatorChar)).ToArray();
        if (firstSegments.Any(segments => segments.Length < 2) || firstSegments.Select(segments => segments[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            return;
        }

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var segments = file.RelativePath.Split(Path.DirectorySeparatorChar);
            files[index] = file with { RelativePath = string.Join(Path.DirectorySeparatorChar, segments.Skip(1)) };
        }
    }

    internal static bool IsLinkOrSpecialEntry(IEntry entry, string format)
    {
        if (entry.LinkTarget is not null)
        {
            return true;
        }

        if (format == "zip" && entry.Attrib is int externalAttributes)
        {
            var unixType = (unchecked((uint)externalAttributes) >> 16) & 0xF000;
            if (unixType != 0 && unixType != 0x8000 && !(entry.IsDirectory && unixType == 0x4000))
            {
                return true;
            }
        }

        if (entry is SevenZipEntry sevenZipEntry &&
            (HasUnsafeSevenZipAttributes(sevenZipEntry.Attrib, entry.IsDirectory) ||
             HasUnsafeSevenZipAttributes(sevenZipEntry.ExtendedAttrib, entry.IsDirectory)))
        {
            return true;
        }

        if (entry is TarEntry tarEntry)
        {
            var entryType = GetTarEntryTypeName(tarEntry);
            if (entryType is not "File" and not "OldFile" and not "Directory")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsafeSevenZipAttributes(int? attributes, bool isDirectory)
    {
        if (attributes is null)
        {
            return false;
        }

        var value = unchecked((uint)attributes.Value);
        if ((value & 0x00000400) != 0) // FILE_ATTRIBUTE_REPARSE_POINT
        {
            return true;
        }

        var unixType = (value >> 16) & 0xF000;
        return unixType != 0 && unixType != 0x8000 && !(isDirectory && unixType == 0x4000);
    }

    private static string GetTarEntryTypeName(TarEntry entry)
    {
        try
        {
            var filePart = TarFilePartField?.GetValue(entry);
            var header = filePart is null ? null : TarHeaderProperty?.GetValue(filePart);
            var entryType = header is null ? null : TarEntryTypeProperty?.GetValue(header);
            return entryType?.ToString() ?? throw new InvalidDataException("TAR girdisinin türü doğrulanamadı; arşiv güvenlik için reddedildi.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or TargetException or TargetInvocationException)
        {
            throw new InvalidDataException("TAR girdisinin türü doğrulanamadı; arşiv güvenlik için reddedildi.", exception);
        }
    }

    private void CountEntry(ref int count)
    {
        if (++count > _limits.MaximumEntries)
        {
            throw new InvalidDataException($"Kaynaklar {_limits.MaximumEntries:N0} arşiv girdisi sınırını aşıyor.");
        }
    }

    internal static string ValidateArchiveEntryPath(string? entryPath, string archivePath, string format)
    {
        if (string.IsNullOrWhiteSpace(entryPath) && (format is "gz" or "bz2" or "xz"))
        {
            var fallback = Path.GetFileNameWithoutExtension(Path.GetFileName(archivePath));
            if (string.IsNullOrWhiteSpace(fallback))
            {
                fallback = "çıkarılan-dosya";
            }

            return ValidateRelativePath(fallback);
        }

        return ValidateRelativePath(entryPath);
    }

    private static string GetSupportedFormat(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".zip" => "zip",
            ".7z" => "7z",
            ".rar" => "rar",
            ".tar" => "tar",
            ".gz" => "gz",
            ".bz2" => "bz2",
            ".xz" => "xz",
            _ => throw new NotSupportedException($"'{extension}' dosya biçimi desteklenmiyor. Desteklenenler: ZIP, 7z, RAR, TAR, GZ, BZ2 ve XZ.")
        };
    }

    private static string ValidateRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Arşiv veya klasör geçersiz bir dosya yolu içeriyor.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.StartsWith("//", StringComparison.Ordinal) || normalized.Contains(':') || Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException($"Mutlak veya sürücü yolu reddedildi: {path}");
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Length > 64 || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"Güvensiz arşiv yolu reddedildi: {path}");
        }

        foreach (var segment in segments)
        {
            if (segment.Length > 240 || segment.EndsWith(' ') || segment.EndsWith('.') || segment.Any(character => character < 32 || Path.GetInvalidFileNameChars().Contains(character)))
            {
                throw new InvalidDataException($"Windows'ta geçersiz dosya adı reddedildi: {path}");
            }

            var deviceStem = segment.Split('.')[0];
            if (IsWindowsDeviceName(deviceStem))
            {
                throw new InvalidDataException($"Windows aygıt adına benzeyen dosya adı reddedildi: {path}");
            }
        }

        var safePath = string.Join(Path.DirectorySeparatorChar, segments);
        if (safePath.Length > 240)
        {
            throw new InvalidDataException($"Dosya yolu Windows uzunluk sınırını aşıyor: {path}");
        }

        return safePath;
    }

    private sealed class QuotaHashingWriteStream(
        Stream destination,
        IncrementalHash hash,
        long previousTotalBytes,
        FileOperationLimits limits,
        CancellationToken cancellationToken) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => destination.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimits(count);
            destination.Write(buffer, offset, count);
            hash.AppendData(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimits(buffer.Length);
            destination.Write(buffer);
            hash.AppendData(buffer);
            BytesWritten += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            EnsureWithinLimits(buffer.Length);
            await destination.WriteAsync(buffer, token).ConfigureAwait(false);
            hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            cancellationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The archive library receives this as a destination, so its lifetime remains with the caller.
            base.Dispose(disposing);
        }

        private void EnsureWithinLimits(int incomingBytes)
        {
            if (incomingBytes < 0 || incomingBytes > limits.MaximumFileBytes - BytesWritten ||
                incomingBytes > limits.MaximumTotalBytes - previousTotalBytes - BytesWritten)
            {
                throw new InvalidDataException("Arşivden çıkarılan veri güvenlik boyut sınırını aşıyor.");
            }
        }
    }

    private static bool IsWindowsDeviceName(string stem)
    {
        var name = stem.TrimEnd(' ', '.').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
               name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9';
    }

    private static string CombineSafePath(string root, string relativePath)
    {
        var safeRelative = ValidateRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, safeRelative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Hedef dışına çıkan yol reddedildi: {relativePath}");
        }

        return fullPath;
    }

    private static void RejectReparseComponents(string path, bool allowMissingTail)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("Hedef yolun kökü belirlenemedi.");
        var current = root;
        var tail = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var missing = false;
        foreach (var segment in tail)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                missing = true;
                if (allowMissingTail)
                {
                    continue;
                }
            }

            if (missing)
            {
                throw new DirectoryNotFoundException($"Hedef klasör bulunamadı: {current}");
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Hedef yolunda bağlantı veya junction reddedildi: {current}");
            }
        }
    }

    private static bool HasReparsePointBetween(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var current = fullRoot;
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            var relative = Path.GetRelativePath(fullRoot, fullPath);
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    private static void RejectReparseFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Hedefte bağlantı dosyası reddedildi: {path}");
        }
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static bool IsPasswordException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Cryptography.CryptographicException || current.GetType().FullName == "SharpCompress.Common.CryptographicException")
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("password", StringComparison.OrdinalIgnoreCase) || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || message.Contains("parola", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        const int maximumAttempts = 5;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (exception is UnauthorizedAccessException || attempt == maximumAttempts - 1)
                {
                    return;
                }

                // Antivirus scanners and recently terminated worker processes can briefly
                // retain a staging payload handle; retry before leaving temporary files behind.
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
            }
        }
    }
}
