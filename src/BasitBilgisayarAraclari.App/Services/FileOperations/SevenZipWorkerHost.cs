using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

internal static class SevenZipWorkerHost
{
    private const string WorkerArgument = "--archive-worker";
    private const string PipePrefix = "BBA7z";
    private const int MaximumPipeConnectMilliseconds = 15_000;

    internal static bool IsWorkerInvocation(string[] arguments) =>
        arguments.Length == 2 &&
        string.Equals(arguments[0], WorkerArgument, StringComparison.Ordinal) &&
        IsWorkerPipeName(arguments[1]);

    internal static async Task<int> RunAsync(string pipeName)
    {
        if (!IsWorkerPipeName(pipeName))
        {
            return 2;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(MaximumPipeConnectMilliseconds).ConfigureAwait(false);
            var request = await SevenZipWorkerProtocol.ReadAsync<SevenZipWorkerRequest>(
                pipe,
                SevenZipWorkerProtocol.MaximumRequestBytes,
                CancellationToken.None).ConfigureAwait(false);

            var response = Extract(request);
            await SevenZipWorkerProtocol.WriteAsync(
                pipe,
                response,
                SevenZipWorkerProtocol.MaximumResponseBytes,
                CancellationToken.None).ConfigureAwait(false);
            return response.Success ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return 2;
        }
    }

    private static SevenZipWorkerResponse Extract(SevenZipWorkerRequest request)
    {
        try
        {
            ValidateRequest(request);
            using var archive = SevenZipArchive.OpenArchive(request.ArchivePath, new ReaderOptions { Password = request.Password });
            var files = new List<SevenZipWorkerPayload>();
            var entryCount = 0;
            var totalBytes = 0L;
            var order = 0;

            foreach (var entry in archive.Entries)
            {
                if (++entryCount > request.MaximumEntries)
                {
                    throw new InvalidDataException("Arşiv izin verilen girdi sayısı sınırını aşıyor.");
                }

                if (entry is null)
                {
                    throw new InvalidDataException("Arşiv girdisi okunamadı.");
                }

                if (FileOperationService.IsLinkOrSpecialEntry(entry, "7z"))
                {
                    throw new InvalidDataException($"Arşiv bağlantı veya özel dosya girdisi içeriyor: {entry.Key}");
                }

                if (entry.IsDirectory)
                {
                    var directoryPath = (entry.Key ?? string.Empty).TrimEnd('/', '\\');
                    if (directoryPath.Length > 0)
                    {
                        _ = FileOperationService.ValidateArchiveEntryPath(directoryPath, request.ArchivePath, "7z");
                    }

                    continue;
                }

                var relativePath = FileOperationService.ValidateArchiveEntryPath(entry.Key, request.ArchivePath, "7z");
                if (entry.IsEncrypted && string.IsNullOrEmpty(request.Password))
                {
                    throw new ArchivePasswordRequiredException(request.ArchivePath, suppliedPasswordWasRejected: false);
                }

                if (entry.Size is > 0 && entry.Size > request.MaximumFileBytes)
                {
                    throw new InvalidDataException($"Arşiv girdisi boyut sınırını aşıyor: {relativePath}");
                }

                var payloadName = Guid.NewGuid().ToString("N") + ".payload";
                var payloadPath = Path.Combine(request.StagingDirectory, payloadName);
                using var output = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var boundedOutput = new WorkerQuotaHashStream(output, hash, request.MaximumFileBytes, request.MaximumTotalBytes - totalBytes);
                entry.WriteTo(boundedOutput, new ExtractionOptions { CheckCrc = true });
                boundedOutput.Flush();

                if (entry.Size is >= 0 && boundedOutput.BytesWritten != entry.Size)
                {
                    throw new InvalidDataException($"Arşiv girdisinin bildirilen boyutu ile okunan boyutu uyuşmuyor: {relativePath}");
                }

                var digest = Convert.ToHexString(hash.GetHashAndReset());
                totalBytes = checked(totalBytes + boundedOutput.BytesWritten);
                files.Add(new SevenZipWorkerPayload(relativePath, payloadName, digest, boundedOutput.BytesWritten, order++));
            }

            return new SevenZipWorkerResponse(true, entryCount, files);
        }
        catch (ArchivePasswordRequiredException exception)
        {
            return Failure("Password", "Arşiv parolası gerekiyor veya kabul edilmedi.", exception.SuppliedPasswordWasRejected);
        }
        catch (Exception exception) when (FileOperationService.IsPasswordException(exception))
        {
            return Failure("Password", "Arşiv parolası gerekiyor veya kabul edilmedi.", !string.IsNullOrEmpty(request.Password));
        }
        catch (SharpCompressException)
        {
            return Failure("InvalidArchive", "7z arşivi okunamadı veya bozuk.");
        }
        catch (InvalidDataException exception)
        {
            return Failure("InvalidData", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or OverflowException)
        {
            return Failure("WorkerError", "7z işçi işlemi tamamlayamadı: " + exception.Message);
        }
    }

    private static void ValidateRequest(SevenZipWorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ArchivePath) ||
            string.IsNullOrWhiteSpace(request.StagingDirectory) ||
            request.MaximumEntries <= 0 ||
            request.MaximumFileBytes <= 0 ||
            request.MaximumTotalBytes < 0)
        {
            throw new InvalidDataException("7z işçi isteğinin sınırları geçersiz.");
        }

        var archivePath = Path.GetFullPath(request.ArchivePath);
        var archiveAttributes = File.GetAttributes(archivePath);
        if ((archiveAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("7z kaynak dosyası normal bir dosya olmalıdır.");
        }

        var stagingDirectory = Path.GetFullPath(request.StagingDirectory);
        var stagingAttributes = File.GetAttributes(stagingDirectory);
        if ((stagingAttributes & FileAttributes.Directory) == 0 || (stagingAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("7z geçici çıkarma klasörü güvenli değil.");
        }
    }

    private static SevenZipWorkerResponse Failure(string code, string message, bool passwordRejected = false) =>
        new(false, 0, [], code, message.Length <= 2048 ? message : message[..2048], passwordRejected);

    private static bool IsWorkerPipeName(string value) =>
        value.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value.AsSpan(PipePrefix.Length), "N", out _);

    private sealed class WorkerQuotaHashStream(
        Stream destination,
        IncrementalHash hash,
        long maximumFileBytes,
        long remainingTotalBytes) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimits(count);
            destination.Write(buffer, offset, count);
            hash.AppendData(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimits(buffer.Length);
            destination.Write(buffer);
            hash.AppendData(buffer);
            BytesWritten += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWithinLimits(buffer.Length);
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing) => base.Dispose(disposing);

        private void EnsureWithinLimits(int count)
        {
            if (count < 0 || count > maximumFileBytes - BytesWritten || count > remainingTotalBytes - BytesWritten)
            {
                throw new InvalidDataException("7z arşivinden çıkarılan veri güvenlik boyut sınırını aşıyor.");
            }
        }
    }
}
