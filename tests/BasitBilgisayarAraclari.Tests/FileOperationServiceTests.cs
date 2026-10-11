using System.IO.Compression;
using System.Reflection;
using System.Text;
using BasitBilgisayarAraclari.App.Services.FileOperations;
using SharpCompress.Common;
using SharpCompress.Writers.Tar;

namespace BasitBilgisayarAraclari.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;
    private readonly FileOperationService _service;

    public FileOperationServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dataRoot = Path.Combine(_root, "app-data");
        _service = new FileOperationService(_dataRoot);
    }

    [Fact]
    public async Task PreviewAndApply_MergesFolderAndArchiveAtCommonTargetPaths()
    {
        var folder = Path.Combine(_root, "folder-source");
        Directory.CreateDirectory(Path.Combine(folder, "bin", "x64"));
        await File.WriteAllTextAsync(Path.Combine(folder, "bin", "x64", "folder.dll"), "folder-content");
        var archive = CreateZip("mod.zip", ("bin/x64/mod.dll", "archive-content"));
        var target = Path.Combine(_root, "game");

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [folder, archive],
            target,
            skipSingleTopFolder: false);

        Assert.Contains(preview.Entries, entry => entry.RelativePath == Path.Combine("bin", "x64", "folder.dll"));
        Assert.Contains(preview.Entries, entry => entry.RelativePath == Path.Combine("bin", "x64", "mod.dll"));
        Assert.False(Directory.Exists(target));

        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts);

        Assert.Equal(2, result.AppliedFiles);
        Assert.Equal("folder-content", await File.ReadAllTextAsync(Path.Combine(target, "bin", "x64", "folder.dll")));
        Assert.Equal("archive-content", await File.ReadAllTextAsync(Path.Combine(target, "bin", "x64", "mod.dll")));
    }

    [Fact]
    public async Task Preview_CanStripOneCommonTopFolderFromEachSource()
    {
        var archive = CreateZip("wrapped-mod.zip", ("ModPack/bin/x64/mod.dll", "mod"));

        using var preview = await _service.PreviewAsync(
            FileOperationKind.GameModInstall,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: true);

        Assert.Equal(Path.Combine("bin", "x64", "mod.dll"), Assert.Single(preview.Entries).RelativePath);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("/outside.txt")]
    public async Task Preview_RejectsArchiveTraversalAndAbsolutePaths(string entryPath)
    {
        var archive = CreateZip("unsafe.zip", (entryPath, "must-not-escape"));
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false));

        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public async Task Preview_RejectsUnixSymbolicLinksStoredInZipAttributes()
    {
        var archive = Path.Combine(_root, "symlink.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var link = zip.CreateEntry("x64/link");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(link.Open());
            writer.Write("../outside.txt");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));
    }

    [Fact]
    public async Task Preview_RejectsTarSymbolicLinkEntries()
    {
        var archive = Path.Combine(_root, "symlink.tar");
        await File.WriteAllBytesAsync(archive, CreateTarLink("x64/link", "../outside.txt", '2'));

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));
    }

    [Fact]
    public async Task Preview_RejectsTarHardLinkEntries()
    {
        var archive = Path.Combine(_root, "hardlink.tar");
        await File.WriteAllBytesAsync(archive, CreateTarLink("x64/alias", "x64/target", '1'));

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));
    }

    [Fact]
    public async Task Preview_RejectsTarFifoEntries()
    {
        var archive = Path.Combine(_root, "fifo.tar");
        await File.WriteAllBytesAsync(archive, CreateTarSpecialEntry("x64/pipe", '6'));

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));
    }

    [Fact]
    public async Task Preview_RejectsStoredZipPayloadWhoseCrcDoesNotMatch()
    {
        var archive = Path.Combine(_root, "tampered.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("x64/mod.bin", CompressionLevel.NoCompression).Open())
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes("verified-payload"));
        }

        var bytes = await File.ReadAllBytesAsync(archive);
        var fileNameLength = BitConverter.ToUInt16(bytes, 26);
        var extraLength = BitConverter.ToUInt16(bytes, 28);
        var payloadOffset = 30 + fileNameLength + extraLength;
        bytes[payloadOffset] ^= 0x01;
        await File.WriteAllBytesAsync(archive, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));

        Assert.False(File.Exists(Path.Combine(_root, "target", "x64", "mod.bin")));
    }

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000), false, true)]
    [InlineData(0x00000400, false, true)]
    [InlineData(unchecked((int)0x41ED0010), true, false)]
    [InlineData(unchecked((int)0x81A40020), false, false)]
    public void SevenZipAttributes_RejectLinksAndAllowRegularFilesAndDirectories(int attributes, bool isDirectory, bool expectedUnsafe)
    {
        var method = typeof(FileOperationService).GetMethod("HasUnsafeSevenZipAttributes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (bool)method.Invoke(null, [attributes, isDirectory])!;

        Assert.Equal(expectedUnsafe, result);
    }

    [Fact]
    public async Task Preview_ExtractsSevenZipArchiveEntries()
    {
        const string sevenZipFixture = "N3q8ryccAAQmcH4pIAAAAAAAAABiAAAAAAAAAITP1VsBABvvu792ZXJpZmllZC1zZXZlbnppcC1jb250ZW50AAEEBgABCSAABwsBAAEhIQEADBwACAoBdbK9sAAABQEZDAAAAAAAAAAAAAAAABEZAHAAYQB5AGwAbwBhAGQALgB0AHgAdAAAABkCAAAUCgEAsn+JLPBY3QEVBgEAIAAAAAAA";
        var archive = Path.Combine(_root, "verified.7z");
        await File.WriteAllBytesAsync(archive, Convert.FromBase64String(sevenZipFixture));

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false);

        var entry = Assert.Single(preview.Entries);
        Assert.Equal("payload.txt", entry.RelativePath);
        Assert.True(entry.SizeBytes > 0);
    }

    [Fact]
    public async Task Preview_RejectsSevenZipSymbolicLinkFixture()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "7z-symbolic-link.7z");
        Assert.True(File.Exists(fixture), $"Expected 7z symbolic-link fixture at {fixture}");
        var archive = Path.Combine(_root, "symlink.7z");
        File.Copy(fixture, archive);
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false));

        Assert.Contains("Arşiv bağlantı veya özel dosya girdisi içeriyor: link.txt", exception.Message);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(target, "link.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "referent.txt")));
    }

    [Fact]
    public async Task Preview_RejectsCorruptSevenZipThroughWorkerAndCleansStaging()
    {
        var stagingRoot = Path.Combine(_root, "worker-staging");
        var archive = Path.Combine(_root, "broken.7z");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        await File.WriteAllBytesAsync(archive, Array.Empty<byte>());

        var service = new FileOperationService(_dataRoot, new FileOperationLimits(), stagingRoot, workerLauncher: null);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false));

        Assert.Contains("arşivi okunamadı veya bozuk", exception.Message);
        Assert.True(Directory.Exists(stagingRoot));
        Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
        Assert.Empty(Directory.EnumerateFiles(stagingRoot));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(target, "payload.txt")));
    }

    [Fact]
    public async Task PreviewAndApply_ExtractsRarFixture()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Rar.none.rar");
        Assert.True(File.Exists(fixture), $"Expected RAR fixture at {fixture}");
        var archive = Path.Combine(_root, "verified.rar");
        var target = Path.Combine(_root, "rar-target");
        File.Copy(fixture, archive);

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false);

        Assert.NotEmpty(preview.Entries);
        Assert.False(Directory.Exists(target));

        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts);

        Assert.Equal(preview.Entries.Count, result.AppliedFiles);
        foreach (var entry in preview.Entries)
        {
            var extractedPath = Path.Combine(target, entry.RelativePath);
            Assert.True(File.Exists(extractedPath), $"Expected extracted RAR entry at {extractedPath}");
            Assert.Equal(entry.SizeBytes, new FileInfo(extractedPath).Length);
        }
    }

    [Fact]
    public async Task Preview_RequestsPasswordForEncryptedSevenZipArchiveAndAcceptsCorrectPassword()
    {
        // Generated with 7-Zip 26.04; AES-encrypted headers, password is a non-secret test constant.
        const string encryptedSevenZipFixture = "N3q8ryccAAR+ONI1oAAAAAAAAAAvAAAAAAAAAHVw2rOOlKcZxuw/7YrVoHu4uARKUQIExB2YrXSndqgqR9ZEwiYAwW0ic+RCvDBNhkXOtKH6orPQ05yBN8Gei755Kh2yZY/CUA2S7fpbmN3I6hT7oZ/F1JdQdtm5X6zko2ZxdxI+G7BWxiCOKXaiVop77PJy306poRFuzq9whlNaC/VZ/WNuIR/lAj9KipzKOqCzDENNWjYXQiULfjVSJwjQCpQTFwYgAQmAgAAHCwEAASQG8QcBElMP2oA97E6ZBLnW7Wt2xP2+CgxyCgG+gnA6AAA=";
        const string password = "BBA-Fixture-Password";
        var archive = Path.Combine(_root, "passworded.7z");
        var target = Path.Combine(_root, "target");
        await File.WriteAllBytesAsync(archive, Convert.FromBase64String(encryptedSevenZipFixture));

        var missingPassword = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false));
        Assert.False(missingPassword.SuppliedPasswordWasRejected);

        var wrongPassword = await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false,
            archivePasswords: new Dictionary<string, string> { [archive] = "wrong-password" }));
        Assert.True(wrongPassword.SuppliedPasswordWasRejected);

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false,
            archivePasswords: new Dictionary<string, string> { [archive] = password });

        var entry = Assert.Single(preview.Entries);
        Assert.Equal("payload.txt", entry.RelativePath);
        await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts);
        Assert.Equal("passworded-fixture-payload", await File.ReadAllTextAsync(Path.Combine(target, "payload.txt")));
    }

    [Fact]
    public async Task Preview_UsesArchiveNameForGzipWithoutOriginalFilename()
    {
        var archive = Path.Combine(_root, "nameless.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("compressed-content"));
        }

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false);

        Assert.Equal("nameless", Assert.Single(preview.Entries).RelativePath);
    }

    [Fact]
    public async Task Preview_RejectsGzipWithCorruptCrcFooter()
    {
        var archive = Path.Combine(_root, "tampered.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("verified-gzip-payload"));
        }

        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[^8] ^= 0x01;
        await File.WriteAllBytesAsync(archive, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));
    }

    [Fact]
    public async Task Preview_ExtractsGzipCompressedTarEntriesInsteadOfSavingTarPayload()
    {
        var archive = Path.Combine(_root, "wrapped.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        {
            await gzip.WriteAsync(CreateTarRegularFile("ModPack/x64/mod.dll", "tar-mod-content"));
        }

        var target = Path.Combine(_root, "target");
        using var preview = await _service.PreviewAsync(
            FileOperationKind.GameModInstall,
            [archive],
            target,
            skipSingleTopFolder: true);

        Assert.Equal(Path.Combine("x64", "mod.dll"), Assert.Single(preview.Entries).RelativePath);
        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts, "Tar game");
        Assert.Equal("tar-mod-content", await File.ReadAllTextAsync(Path.Combine(target, "x64", "mod.dll")));
        Assert.NotNull(result.BackupId);
    }

    [Fact]
    public async Task Preview_ExtractsPlainTarEntries()
    {
        var archive = Path.Combine(_root, "plain.tar");
        await File.WriteAllBytesAsync(archive, CreateTarRegularFile("x64/mod.dll", "plain-tar-content"));

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false);

        Assert.Equal(Path.Combine("x64", "mod.dll"), Assert.Single(preview.Entries).RelativePath);
        await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts);
        Assert.Equal("plain-tar-content", await File.ReadAllTextAsync(Path.Combine(_root, "target", "x64", "mod.dll")));
    }

    [Theory]
    [InlineData(CompressionType.BZip2, "tar.bz2")]
    public async Task Preview_ExtractsBzip2AndXzCompressedTarEntries(CompressionType compression, string extension)
    {
        var archive = Path.Combine(_root, "compressed." + extension);
        using (var file = File.Create(archive))
        using (var writer = new TarWriter(file, new TarWriterOptions(compression, finalizeArchiveOnClose: true)))
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes("compressed-tar-content")))
        {
            writer.Write("ModPack/x64/mod.dll", content, modificationTime: null);
        }

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: true);

        Assert.Equal(Path.Combine("x64", "mod.dll"), Assert.Single(preview.Entries).RelativePath);
    }

    [Fact]
    public async Task Preview_ExtractsXzCompressedTarEntries()
    {
        const string xzFixture = "/Td6WFoAAAFpIt42AgAhAQAAAAA3J5fW4Af/AGBdACabyIUJ/ufhh99bqlN+2Y0sp719eo/r7BDqDaYe2FjX0fJoK9jHuEfQpSrprKLRgge2jEsDm/SwOXpLwygyyDWyHH7jas7Ail2Zx9ac0qXzO/cD4yViarM7AE+XZpy6AABObdRKAAF4gBAAAAB8vNvFPjANiwIAAAAAAVla";
        var archive = Path.Combine(_root, "compressed.tar.xz");
        await File.WriteAllBytesAsync(archive, Convert.FromBase64String(xzFixture));

        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: true);

        Assert.Equal(Path.Combine("x64", "mod.dll"), Assert.Single(preview.Entries).RelativePath);
    }

    [Fact]
    public async Task Preview_ReportsCorruptArchiveAsInvalidData()
    {
        var archive = Path.Combine(_root, "broken.zip");
        await File.WriteAllTextAsync(archive, "not an archive");

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false));
    }

    [Fact]
    public async Task Apply_SkipPolicySkipsDifferentSourceContent()
    {
        var first = CreateZip("first.zip", ("x64/mod.bin", "first"));
        var second = CreateZip("second.zip", ("x64/mod.bin", "second"));
        var target = Path.Combine(_root, "target");
        using var preview = await _service.PreviewAsync(FileOperationKind.ExtractAndMerge, [first, second], target, false);
        var item = Assert.Single(preview.Entries);
        Assert.True(item.HasSourceConflict);

        var result = await _service.ApplyAsync(preview, ConflictPolicy.SkipConflicts);

        Assert.Equal(0, result.AppliedFiles);
        Assert.Equal(1, result.SkippedFiles);
        Assert.False(File.Exists(Path.Combine(target, "x64", "mod.bin")));
    }

    [Fact]
    public async Task Preview_CollapsesIdenticalFilesFromMultipleSources()
    {
        var first = CreateZip("first-identical.zip", ("x64/mod.bin", "same"));
        var second = CreateZip("second-identical.zip", ("x64/mod.bin", "same"));
        using var preview = await _service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [first, second],
            Path.Combine(_root, "target"),
            skipSingleTopFolder: false);

        Assert.False(Assert.Single(preview.Entries).HasSourceConflict);
    }

    [Fact]
    public async Task GameModOverwrite_CreatesRestorableBackup()
    {
        var target = Path.Combine(_root, "game");
        var destination = Path.Combine(target, "x64", "mod.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "original-game-file");
        var archive = CreateZip("new-mod.zip", ("x64/mod.bin", "modded-file"));
        using var preview = await _service.PreviewAsync(FileOperationKind.GameModInstall, [archive], target, false);

        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts, "Test game");

        Assert.Equal("modded-file", await File.ReadAllTextAsync(destination));
        Assert.NotNull(result.BackupId);
        Assert.Equal(1, result.BackedUpFiles);
        Assert.Contains(_service.GetBackups(), backup => backup.Id == result.BackupId);

        var restore = await _service.RestoreBackupAsync(result.BackupId!.Value);

        Assert.True(restore.IsFullyRestored);
        Assert.Equal("original-game-file", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task GameModInstall_TracksNewFilesSoRestoreCanRemoveThem()
    {
        var target = Path.Combine(_root, "empty-game");
        var archive = CreateZip("add-mod.zip", ("x64/new-mod.bin", "new-mod"));
        using var preview = await _service.PreviewAsync(FileOperationKind.GameModInstall, [archive], target, false);
        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts, "Empty game");

        Assert.NotNull(result.BackupId);
        var newFile = Path.Combine(target, "x64", "new-mod.bin");
        Assert.True(File.Exists(newFile));

        var restore = await _service.RestoreBackupAsync(result.BackupId!.Value);

        Assert.True(restore.IsFullyRestored);
        Assert.False(File.Exists(newFile));
    }

    [Fact]
    public async Task RestoreBackup_PreservesFilesChangedAfterModInstall()
    {
        var target = Path.Combine(_root, "game");
        var destination = Path.Combine(target, "x64", "mod.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "original");
        var archive = CreateZip("changed-after-install.zip", ("x64/mod.bin", "installed"));
        using var preview = await _service.PreviewAsync(FileOperationKind.GameModInstall, [archive], target, false);
        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts, "Changed game");
        await File.WriteAllTextAsync(destination, "user changed this later");

        var restore = await _service.RestoreBackupAsync(result.BackupId!.Value);

        Assert.False(restore.IsFullyRestored);
        Assert.Equal(1, restore.SkippedChangedFiles);
        Assert.Equal("user changed this later", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task RestoreBackup_RejectsTamperedBackupPayloadWithoutChangingInstalledFile()
    {
        var target = Path.Combine(_root, "game");
        var destination = Path.Combine(target, "x64", "mod.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "original");
        var archive = CreateZip("tampered-backup.zip", ("x64/mod.bin", "installed"));
        using var preview = await _service.PreviewAsync(FileOperationKind.GameModInstall, [archive], target, false);
        var result = await _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts, "Tamper game");

        var backupDirectory = Path.Combine(_dataRoot, "backups", result.BackupId!.Value.ToString("N"));
        var backupPayload = Assert.Single(Directory.EnumerateFiles(backupDirectory, "*.backup"));
        await File.WriteAllTextAsync(backupPayload, "corrupted-backup");

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RestoreBackupAsync(result.BackupId.Value));

        Assert.Equal("installed", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Apply_StopsIfDestinationChangedAfterPreview()
    {
        var target = Path.Combine(_root, "target");
        var archive = CreateZip("mod.zip", ("x64/mod.bin", "staged-content"));
        using var preview = await _service.PreviewAsync(FileOperationKind.ExtractAndMerge, [archive], target, false);
        var destination = Path.Combine(target, "x64", "mod.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, "concurrent-user-file");

        await Assert.ThrowsAsync<IOException>(() => _service.ApplyAsync(preview, ConflictPolicy.OverwriteConflicts));

        Assert.Equal("concurrent-user-file", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Preview_RejectsOversizedArchiveEntryWithoutChangingTarget()
    {
        var archive = CreateZip("large.zip", ("x64/large.bin", "12345"));
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        var limited = new FileOperationService(
            _dataRoot,
            new FileOperationLimits(MaximumEntries: 10, MaximumFileBytes: 4, MaximumTotalBytes: 8));

        await Assert.ThrowsAsync<InvalidDataException>(() => limited.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archive],
            target,
            skipSingleTopFolder: false));

        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.False(File.Exists(Path.Combine(target, "x64", "large.bin")));
    }

    [Fact]
    public void GameProfileStore_SavesProfilesAndRecentFoldersLocally()
    {
        var store = new GameProfileStore(Path.Combine(_root, "profiles"));
        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);

        store.SaveProfile("  Test Game  ", gameFolder, skipSingleTopFolder: true);
        store.RememberPaths([gameFolder], gameFolder);

        var profile = Assert.Single(store.GetProfiles());
        Assert.Equal("Test Game", profile.Name);
        Assert.True(profile.SkipSingleTopFolder);
        Assert.Equal(gameFolder, Assert.Single(store.GetRecentPaths().Sources));
    }

    private string CreateZip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            using var stream = archive.CreateEntry(entryPath).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }

        return path;
    }

    private static byte[] CreateTarLink(string name, string linkTarget, char typeFlag)
    {
        var header = new byte[512];
        WriteTarField(header, 0, 100, name);
        var mode = typeFlag == '2' ? 0xA1FF : 0x81A4;
        WriteTarField(header, 100, 8, Convert.ToString(mode, 8)!);
        WriteTarField(header, 108, 8, "0");
        WriteTarField(header, 116, 8, "0");
        WriteTarField(header, 124, 12, "0");
        WriteTarField(header, 136, 12, "0");
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)typeFlag;
        WriteTarField(header, 157, 100, linkTarget);
        WriteTarField(header, 257, 6, "ustar\0");
        WriteTarField(header, 263, 2, "00");
        var checksum = header.Sum(value => (int)value);
        WriteTarField(header, 148, 6, Convert.ToString(checksum, 8)!.PadLeft(6, '0'));
        header[154] = 0;
        header[155] = (byte)' ';
        return [.. header, .. new byte[1024]];
    }

    private static byte[] CreateTarSpecialEntry(string name, char typeFlag)
    {
        var header = new byte[512];
        WriteTarField(header, 0, 100, name);
        WriteTarField(header, 100, 8, Convert.ToString(0x180, 8)!);
        WriteTarField(header, 108, 8, "0");
        WriteTarField(header, 116, 8, "0");
        WriteTarField(header, 124, 12, "0");
        WriteTarField(header, 136, 12, "0");
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)typeFlag;
        WriteTarField(header, 257, 6, "ustar\0");
        WriteTarField(header, 263, 2, "00");
        var checksum = header.Sum(value => (int)value);
        WriteTarField(header, 148, 6, Convert.ToString(checksum, 8)!.PadLeft(6, '0'));
        header[154] = 0;
        header[155] = (byte)' ';
        return [.. header, .. new byte[1024]];
    }

    private static byte[] CreateTarRegularFile(string name, string content)
    {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var header = new byte[512];
        WriteTarField(header, 0, 100, name);
        WriteTarField(header, 100, 8, Convert.ToString(0x81A4, 8)!);
        WriteTarField(header, 108, 8, "0");
        WriteTarField(header, 116, 8, "0");
        WriteTarField(header, 124, 12, Convert.ToString(contentBytes.Length, 8)!.PadLeft(11, '0'));
        WriteTarField(header, 136, 12, "0");
        Array.Fill(header, (byte)' ', 148, 8);
        header[156] = (byte)'0';
        WriteTarField(header, 257, 6, "ustar\0");
        WriteTarField(header, 263, 2, "00");
        var checksum = header.Sum(value => (int)value);
        WriteTarField(header, 148, 6, Convert.ToString(checksum, 8)!.PadLeft(6, '0'));
        header[154] = 0;
        header[155] = (byte)' ';
        var paddedContentLength = (contentBytes.Length + 511) / 512 * 512;
        var tar = new byte[512 + paddedContentLength + 1024];
        Array.Copy(header, tar, header.Length);
        Array.Copy(contentBytes, 0, tar, 512, contentBytes.Length);
        return tar;
    }

    private static void WriteTarField(byte[] header, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length));
        if (bytes.Length < length)
        {
            header[offset + bytes.Length] = 0;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup is best effort; the system temp directory is isolated by test ID.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup is best effort; no permissions are changed to force deletion.
        }
    }
}
