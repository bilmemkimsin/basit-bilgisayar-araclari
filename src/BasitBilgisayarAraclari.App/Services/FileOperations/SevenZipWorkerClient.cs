using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using BasitBilgisayarAraclari.App.Localization;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

internal static class SevenZipWorkerClient
{
    private const long WorkerMemoryLimitBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    internal static IReadOnlyList<StagedFile> Extract(
        string archivePath,
        string? password,
        string stagingDirectory,
        string sourceLabel,
        FileOperationLimits limits,
        ref long totalBytes,
        ref int order,
        ref int entryCount,
        CancellationToken cancellationToken) =>
        Extract(archivePath, password, stagingDirectory, sourceLabel, limits,
            ref totalBytes, ref order, ref entryCount, cancellationToken, workerLauncher: null, timeoutOverride: null);

    internal static IReadOnlyList<StagedFile> Extract(
        string archivePath,
        string? password,
        string stagingDirectory,
        string sourceLabel,
        FileOperationLimits limits,
        ref long totalBytes,
        ref int order,
        ref int entryCount,
        CancellationToken cancellationToken,
        Func<string, Process>? workerLauncher,
        TimeSpan? timeoutOverride,
        long? memoryLimitOverride = null)
    {
        workerLauncher ??= StartWorker;
        var remainingEntries = limits.MaximumEntries - entryCount;
        var remainingBytes = limits.MaximumTotalBytes - totalBytes;
        if (remainingEntries <= 0)
        {
            throw new InvalidDataException($"Kaynaklar {limits.MaximumEntries:N0} arşiv girdisi sınırını aşıyor.");
        }

        var request = new SevenZipWorkerRequest(
            Path.GetFullPath(archivePath),
            password,
            Path.GetFullPath(stagingDirectory),
            remainingEntries,
            limits.MaximumFileBytes,
            remainingBytes);
        using var timeout = new CancellationTokenSource();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var response = RunWorkerAsync(
                request,
                cancellationToken,
                operation.Token,
                timeout,
                timeoutOverride ?? WorkerTimeout,
                workerLauncher,
                memoryLimitOverride ?? WorkerMemoryLimitBytes).GetAwaiter().GetResult();
            if (!response.Success)
            {
                throw response.ErrorCode switch
                {
                    "Password" => new ArchivePasswordRequiredException(archivePath, response.PasswordRejected),
                    "InvalidData" => new InvalidDataException(response.ErrorMessage ?? "7z arşiv girdisi geçersiz."),
                    "InvalidArchive" => new InvalidDataException($"'{sourceLabel}' arşivi okunamadı veya bozuk."),
                    "WorkerError" => new IOException(response.ErrorMessage ?? "7z işçi işlemi tamamlanamadı."),
                    _ => new InvalidDataException("7z işçiden geçersiz işlem sonucu alındı.")
                };
            }

            var stagedFiles = ValidatePayloads(response, request, sourceLabel, limits, totalBytes, order, entryCount, operation.Token);
            totalBytes += stagedFiles.Sum(file => file.SizeBytes);
            order += stagedFiles.Count;
            entryCount += response.EntryCount;
            return stagedFiles;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("7z arşivi beş dakika içinde işlenemedi veya doğrulanamadı; geçici çıkarma iptal edildi.");
        }
    }

    private static async Task<SevenZipWorkerResponse> RunWorkerAsync(
        SevenZipWorkerRequest request,
        CancellationToken gateCancellationToken,
        CancellationToken cancellationToken,
        CancellationTokenSource timeout,
        TimeSpan timeoutDuration,
        Func<string, Process> workerLauncher,
        long memoryLimitBytes)
    {
        await WorkerGate.WaitAsync(gateCancellationToken).ConfigureAwait(false);
        // A queued operation must not consume the worker's extraction deadline.
        // This also makes short timeout tests deterministic when other 7z tests run in parallel.
        timeout.CancelAfter(timeoutDuration);
        try
        {
            var pipeName = "BBA7z" + Guid.NewGuid().ToString("N");
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var job = new SevenZipWorkerJob(memoryLimitBytes);
            using var worker = workerLauncher(pipeName);

            try
            {
                job.Assign(worker);
                using var cancellationRegistration = cancellationToken.Register(
                    static state =>
                    {
                        var (workerJob, process) = ((SevenZipWorkerJob, Process))state!;
                        StopWorker(workerJob, process);
                    },
                    (job, worker));

                await WaitForPipeConnectionAsync(pipe, worker, cancellationToken).ConfigureAwait(false);
                await SevenZipWorkerProtocol.WriteAsync(
                    pipe,
                    request,
                    SevenZipWorkerProtocol.MaximumRequestBytes,
                    cancellationToken).ConfigureAwait(false);
                var response = await SevenZipWorkerProtocol.ReadAsync<SevenZipWorkerResponse>(
                    pipe,
                    SevenZipWorkerProtocol.MaximumResponseBytes,
                    cancellationToken).ConfigureAwait(false);
                await worker.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (worker.ExitCode != (response.Success ? 0 : 1))
                {
                    throw new InvalidDataException("7z işçi işlemi beklenmeyen çıkış koduyla sonlandı.");
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                StopWorker(job, worker);
                await worker.WaitForExitAsync().ConfigureAwait(false);
                throw;
            }
            catch (IOException exception)
            {
                if (!worker.HasExited)
                {
                    // A worker can close its pipe just before the process exit becomes visible.
                    // Stop it rather than leaking a raw EndOfStreamException or waiting indefinitely.
                    StopWorker(job, worker);
                }

                await worker.WaitForExitAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidDataException(
                    $"7z işçi yanıt vermeden sonlandı (çıkış kodu {worker.ExitCode}); bellek veya süre sınırına ulaşmış olabilir.",
                    exception);
            }
            catch
            {
                StopWorker(job, worker);
                await worker.WaitForExitAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            WorkerGate.Release();
        }
    }

    private static async Task WaitForPipeConnectionAsync(
        NamedPipeServerStream pipe,
        Process worker,
        CancellationToken cancellationToken)
    {
        var connection = pipe.WaitForConnectionAsync(cancellationToken);
        while (!connection.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (worker.HasExited)
            {
                await worker.WaitForExitAsync().ConfigureAwait(false);
                throw new IOException($"7z işçi bağlantı kurmadan sonlandı (çıkış kodu {worker.ExitCode}).");
            }

            await Task.WhenAny(connection, Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken))
                .ConfigureAwait(false);
        }

        await connection.ConfigureAwait(false);
    }

    private static Process StartWorker(string pipeName)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "BasitBilgisayarAraclari.App.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("7z işçi uygulama dosyası bulunamadı; güvenli çıkarma başlatılamadı.", executable);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.Environment["BBA_UI_LANGUAGE"] = LocalizationManager.Instance.LanguageCode;
        startInfo.ArgumentList.Add("--archive-worker");
        startInfo.ArgumentList.Add(pipeName);
        return Process.Start(startInfo)
            ?? throw new IOException("7z işçi süreci başlatılamadı.");
    }

    private static List<StagedFile> ValidatePayloads(
        SevenZipWorkerResponse response,
        SevenZipWorkerRequest request,
        string sourceLabel,
        FileOperationLimits limits,
        long previousTotalBytes,
        int orderOffset,
        int previousEntryCount,
        CancellationToken cancellationToken)
    {
        if (response.Files is null ||
            response.EntryCount < response.Files.Count ||
            response.EntryCount > limits.MaximumEntries - previousEntryCount ||
            response.Files.Count > limits.MaximumEntries - previousEntryCount)
        {
            throw new InvalidDataException("7z işçi manifesti girdi sınırlarıyla uyuşmuyor.");
        }

        var stagingRoot = Path.GetFullPath(request.StagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seenPayloadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagedFiles = new List<StagedFile>(response.Files.Count);
        var returnedBytes = 0L;
        for (var index = 0; index < response.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = response.Files[index];
            if (payload is null ||
                string.IsNullOrWhiteSpace(payload.RelativePath) ||
                string.IsNullOrWhiteSpace(payload.PayloadName) ||
                string.IsNullOrWhiteSpace(payload.Sha256) ||
                payload.Order != index || payload.SizeBytes < 0 || payload.SizeBytes > limits.MaximumFileBytes ||
                payload.SizeBytes > limits.MaximumTotalBytes - previousTotalBytes - returnedBytes ||
                payload.Sha256.Length != 64 ||
                payload.PayloadName.Length != 40 ||
                !payload.PayloadName.EndsWith(".payload", StringComparison.Ordinal) ||
                payload.PayloadName.AsSpan(0, 32).IndexOfAnyExcept("0123456789abcdef") >= 0 ||
                !seenPayloadNames.Add(payload.PayloadName))
            {
                throw new InvalidDataException("7z işçi manifestinde geçersiz bir dosya kaydı var.");
            }

            var relativePath = FileOperationService.ValidateArchiveEntryPath(payload.RelativePath, request.ArchivePath, "7z");
            _ = Convert.FromHexString(payload.Sha256);
            var payloadPath = Path.GetFullPath(Path.Combine(stagingRoot, payload.PayloadName));
            if (!payloadPath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("7z işçi payload yolu staging klasörünün dışına çıkıyor.");
            }

            var attributes = File.GetAttributes(payloadPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("7z işçi payload dosyası normal bir dosya değil.");
            }

            var info = new FileInfo(payloadPath);
            if (info.Length != payload.SizeBytes)
            {
                throw new InvalidDataException("7z işçi payload boyutu manifest ile uyuşmuyor.");
            }

            var digest = ComputeSha256(payloadPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(payload.Sha256)))
            {
                throw new InvalidDataException("7z işçi payload özeti manifest ile uyuşmuyor.");
            }

            returnedBytes = checked(returnedBytes + payload.SizeBytes);
            stagedFiles.Add(new StagedFile(relativePath, sourceLabel, payloadPath, digest, payload.SizeBytes, orderOffset + index));
        }

        return stagedFiles;
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void StopWorker(SevenZipWorkerJob job, Process worker)
    {
        if (worker.HasExited)
        {
            return;
        }

        try
        {
            job.Terminate();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            // A worker may not have been assigned to the job, so process termination below is still required.
        }

        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Waiting for process exit remains the caller's responsibility.
        }
    }
}
