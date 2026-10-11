using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

internal sealed record SevenZipWorkerRequest(
    string ArchivePath,
    string? Password,
    string StagingDirectory,
    int MaximumEntries,
    long MaximumFileBytes,
    long MaximumTotalBytes);

internal sealed record SevenZipWorkerPayload(
    string RelativePath,
    string PayloadName,
    string Sha256,
    long SizeBytes,
    int Order);

internal sealed record SevenZipWorkerResponse(
    bool Success,
    int EntryCount,
    IReadOnlyList<SevenZipWorkerPayload> Files,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool PasswordRejected = false);

internal static class SevenZipWorkerProtocol
{
    internal const int MaximumRequestBytes = 2 * 1024 * 1024;
    internal const int MaximumResponseBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException("7z işçi ileti boyutu geçersiz veya izin verilen sınırı aşıyor.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("7z işçiden boş yanıt alındı.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, int maximumBytes, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length is <= 0 || payload.Length > maximumBytes)
        {
            throw new InvalidDataException("7z işçi ileti boyutu izin verilen sınırı aşıyor.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
