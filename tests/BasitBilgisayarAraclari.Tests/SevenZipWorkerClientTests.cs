using System.Diagnostics;
using BasitBilgisayarAraclari.App.Services.FileOperations;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace BasitBilgisayarAraclari.Tests;

public sealed class SevenZipWorkerClientTests
{
    [Fact]
    public async Task Preview_CancellationStopsWorkerAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "cancel.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var idleProcessPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(idleProcessPath), $"Expected Windows PowerShell at {idleProcessPath}");

        using var cancellation = new CancellationTokenSource();
        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            _ => StartIdleWorker(idleProcessPath, workerStarted, workerExited));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false,
            cancellationToken: cancellation.Token);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => preview.WaitAsync(TimeSpan.FromSeconds(5)));
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(Directory.Exists(stagingRoot));
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
            Assert.Empty(Directory.EnumerateFiles(stagingRoot));
            Assert.False(Directory.Exists(target));
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            cancellation.Cancel();
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                try
                {
                    await preview.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected while unwinding a cancelled preview.
                }
            }

            if (Directory.Exists(root))
            {
                await DeleteDirectoryWithRetry(root);
            }
        }
    }

    [Fact]
    public async Task Preview_WorkerTimeoutStopsWorkerAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "timeout.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var idleProcessPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(idleProcessPath), $"Expected Windows PowerShell at {idleProcessPath}");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            _ => StartIdleWorker(idleProcessPath, workerStarted, workerExited),
            TimeSpan.FromMilliseconds(200));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => preview.WaitAsync(TimeSpan.FromSeconds(5)));
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStagingCleaned(stagingRoot, target);
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                await preview.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (Directory.Exists(root))
            {
                await DeleteDirectoryWithRetry(root);
            }
        }
    }

    [Fact]
    public async Task Preview_JobMemoryLimitStopsWorkerAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "memory-limit.7z");
        var allocationMarkerPath = Path.Combine(root, "allocation-count.txt");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershellPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershellPath), $"Expected Windows PowerShell at {powershellPath}");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            pipeName => StartMemoryPressureWorker(powershellPath, pipeName, allocationMarkerPath, workerStarted, workerExited),
            TimeSpan.FromSeconds(10),
            192L * 1024 * 1024);
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => preview.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("7z işçi yanıt vermeden sonlandı", exception.Message);

            var exitCode = await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(0, exitCode);
            Assert.True(File.Exists(allocationMarkerPath), "The worker must reach its memory allocation loop after receiving the request.");
            var allocationCount = int.Parse(await File.ReadAllTextAsync(allocationMarkerPath));
            Assert.InRange(allocationCount, 1, 63);
            Assert.True(Directory.Exists(stagingRoot));
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
            Assert.Empty(Directory.EnumerateFiles(stagingRoot));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                try
                {
                    await preview.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (InvalidDataException)
                {
                    // Expected when the worker exceeds its bounded test memory budget.
                }
            }

            if (Directory.Exists(root))
            {
                await DeleteDirectoryWithRetry(root);
            }
        }
    }

    [Fact]
    public async Task Preview_CancellationDuringRealSevenZipExtractionStopsWorkerAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "large-valid.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        CreateCompressedSevenZip(archivePath, 64L * 1024 * 1024);
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            pipeName => StartAppHostWorker(pipeName, workerStarted, workerExited));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false,
            cancellationToken: cancellation.Token);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var extractionObserved = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!preview.IsCompleted && DateTime.UtcNow < deadline)
            {
                foreach (var payloadPath in Directory.EnumerateFiles(stagingRoot, "*.payload", SearchOption.AllDirectories))
                {
                    if (new FileInfo(payloadPath).Length >= 1024 * 1024 && !workerExited.Task.IsCompleted)
                    {
                        extractionObserved = true;
                        cancellation.Cancel();
                        break;
                    }
                }

                if (extractionObserved)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1));
            }

            Assert.True(extractionObserved, "Expected to observe the real 7z worker writing a staged payload before cancellation.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => preview.WaitAsync(TimeSpan.FromSeconds(10)));
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(Directory.Exists(stagingRoot));
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
            Assert.Empty(Directory.EnumerateFiles(stagingRoot));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
            Assert.False(File.Exists(Path.Combine(target, "payload.bin")));
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            cancellation.Cancel();
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                try
                {
                    await preview.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException)
                {
                    // Expected while stopping the parser worker.
                }
            }

            if (Directory.Exists(root))
            {
                await DeleteDirectoryWithRetry(root);
            }
        }
    }

    [Fact]
    public async Task Preview_UnexpectedWorkerExitDuringRealSevenZipExtractionCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "worker-exit-valid.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        CreateCompressedSevenZip(archivePath, 64L * 1024 * 1024);
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            pipeName => StartAppHostWorker(pipeName, workerStarted, workerExited));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var extractionObserved = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!preview.IsCompleted && DateTime.UtcNow < deadline)
            {
                foreach (var payloadPath in Directory.EnumerateFiles(stagingRoot, "*.payload", SearchOption.AllDirectories))
                {
                    if (new FileInfo(payloadPath).Length >= 1024 * 1024 && !workerExited.Task.IsCompleted)
                    {
                        extractionObserved = true;
                        using var worker = Process.GetProcessById(workerPid);
                        worker.Kill(entireProcessTree: true);
                        break;
                    }
                }

                if (extractionObserved)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1));
            }

            Assert.True(extractionObserved, "Expected to stop the real 7z worker after it began writing a staged payload.");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => preview.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("yanıt vermeden sonlandı", exception.Message);
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(Directory.Exists(stagingRoot));
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
            Assert.Empty(Directory.EnumerateFiles(stagingRoot));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinel));
            Assert.False(File.Exists(Path.Combine(target, "payload.bin")));
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                try
                {
                    await preview.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (IOException)
                {
                    // Expected when the worker closes its pipe while the parser is running.
                }
            }

            if (Directory.Exists(root))
            {
                await DeleteDirectoryWithRetry(root);
            }
        }
    }

    [Fact]
    public async Task Preview_QueuedWorkerDoesNotConsumeWorkerTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var firstStagingRoot = Path.Combine(root, "first-staging");
        var secondStagingRoot = Path.Combine(root, "second-staging");
        var firstArchive = Path.Combine(root, "first.7z");
        var secondArchive = Path.Combine(root, "second.7z");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(firstArchive, Array.Empty<byte>());
        await File.WriteAllBytesAsync(secondArchive, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var idleProcessPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(idleProcessPath), $"Expected Windows PowerShell at {idleProcessPath}");

        using var firstCancellation = new CancellationTokenSource();
        var firstWorkerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWorkerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorkerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWorkerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstService = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            firstStagingRoot,
            _ => StartIdleWorker(idleProcessPath, firstWorkerStarted, firstWorkerExited));
        var secondService = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            secondStagingRoot,
            _ => StartIdleWorker(idleProcessPath, secondWorkerStarted, secondWorkerExited),
            TimeSpan.FromMilliseconds(300));

        var firstPreview = firstService.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [firstArchive],
            Path.Combine(root, "first-target"),
            skipSingleTopFolder: false,
            cancellationToken: firstCancellation.Token);
        Task<StagedFileOperation>? secondPreview = null;
        try
        {
            var firstWorkerPid = await firstWorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondPreview = secondService.PreviewAsync(
                FileOperationKind.ExtractAndMerge,
                [secondArchive],
                Path.Combine(root, "second-target"),
                skipSingleTopFolder: false);

            await Task.Delay(TimeSpan.FromMilliseconds(600));
            Assert.False(secondWorkerStarted.Task.IsCompleted, "The queued worker must wait for the shared 7z gate.");
            Assert.False(secondPreview.IsCompleted, "The queued worker timeout must not expire while waiting for the gate.");

            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstPreview.WaitAsync(TimeSpan.FromSeconds(5)));
            await firstWorkerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(IsProcessRunning(firstWorkerPid));

            var secondWorkerPid = await secondWorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => secondPreview.WaitAsync(TimeSpan.FromSeconds(5)));
            await secondWorkerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(IsProcessRunning(secondWorkerPid));
            AssertStagingCleaned(firstStagingRoot, Path.Combine(root, "first-target"));
            AssertStagingCleaned(secondStagingRoot, Path.Combine(root, "second-target"));
        }
        finally
        {
            firstCancellation.Cancel();
            if (firstWorkerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await firstWorkerStarted.Task);
            }

            if (secondWorkerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await secondWorkerStarted.Task);
            }

            if (!firstPreview.IsCompleted)
            {
                try
                {
                    await firstPreview.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected while unwinding the cancelled preview.
                }
            }

            if (secondPreview is { IsCompleted: false })
            {
                try
                {
                    await secondPreview.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException)
                {
                    // Expected while unwinding the worker timeout.
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Preview_WorkerExitBeforePipeConnectionFailsPromptlyAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "crash.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var idleProcessPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(idleProcessPath), $"Expected Windows PowerShell at {idleProcessPath}");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            _ => StartIdleWorker(idleProcessPath, workerStarted, workerExited, "Start-Sleep -Seconds 1; exit 23"));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidDataException>(() => preview.WaitAsync(TimeSpan.FromSeconds(5)));
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStagingCleaned(stagingRoot, target);
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                await preview.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Preview_WorkerExitAfterPipeConnectionFailsAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "crash-after-connect.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var idleProcessPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(idleProcessPath), $"Expected Windows PowerShell at {idleProcessPath}");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            pipeName => StartPipeThenExitWorker(idleProcessPath, pipeName, workerStarted, workerExited));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => preview.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("yanıt vermeden sonlandı", exception.Message);
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStagingCleaned(stagingRoot, target);
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                try
                {
                    await preview.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (IOException)
                {
                    // Expected when the worker closes the pipe without sending a response.
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Preview_RejectsWorkerManifestPathTraversalAndCleansStaging()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "malicious-manifest.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershellPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershellPath), $"Expected Windows PowerShell at {powershellPath}");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            pipeName => StartManifestWorker(powershellPath, pipeName, "traversal", workerStarted, workerExited));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => preview.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Güvensiz arşiv yolu reddedildi", exception.Message);
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStagingCleaned(stagingRoot, target);
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                await preview.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("null-entry")]
    [InlineData("null-path")]
    [InlineData("null-payload-name")]
    [InlineData("null-sha256")]
    public async Task Preview_RejectsMalformedWorkerManifestAndCleansStaging(string manifestEntryKind)
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var stagingRoot = Path.Combine(root, "staging-root");
        var archivePath = Path.Combine(root, "malformed-manifest.7z");
        var target = Path.Combine(root, "game");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(archivePath, Array.Empty<byte>());

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershellPath = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershellPath), $"Expected Windows PowerShell at {powershellPath}");

        var workerStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FileOperationService(
            dataRoot,
            new FileOperationLimits(),
            stagingRoot,
            pipeName => StartManifestWorker(powershellPath, pipeName, manifestEntryKind, workerStarted, workerExited));
        var preview = service.PreviewAsync(
            FileOperationKind.ExtractAndMerge,
            [archivePath],
            target,
            skipSingleTopFolder: false);

        try
        {
            var workerPid = await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => preview.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("7z işçi manifestinde geçersiz bir dosya kaydı", exception.Message);
            await workerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertStagingCleaned(stagingRoot, target);
            Assert.False(IsProcessRunning(workerPid));
        }
        finally
        {
            if (workerStarted.Task.IsCompletedSuccessfully)
            {
                await StopProcessIfRunning(await workerStarted.Task);
            }

            if (!preview.IsCompleted)
            {
                await preview.WaitAsync(TimeSpan.FromSeconds(5));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Process StartIdleWorker(
        string executablePath,
        TaskCompletionSource<int> workerStarted,
        TaskCompletionSource workerExited,
        string command = "Start-Sleep -Seconds 30")
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the idle test worker.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => workerExited.TrySetResult();
        if (process.HasExited)
        {
            workerExited.TrySetResult();
        }

        workerStarted.TrySetResult(process.Id);
        return process;
    }

    private static Process StartPipeThenExitWorker(
        string executablePath,
        string pipeName,
        TaskCompletionSource<int> workerStarted,
        TaskCompletionSource workerExited)
    {
        var command = $"$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', '{pipeName}', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly); $pipe.Connect(5000); Start-Sleep -Milliseconds 750";
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the pipe-connected test worker.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => workerExited.TrySetResult();
        if (process.HasExited)
        {
            workerExited.TrySetResult();
        }

        workerStarted.TrySetResult(process.Id);
        return process;
    }

    private static Process StartMemoryPressureWorker(
        string executablePath,
        string pipeName,
        string allocationMarkerPath,
        TaskCompletionSource<int> workerStarted,
        TaskCompletionSource<int> workerExited)
    {
        var markerLiteral = allocationMarkerPath.Replace("'", "''", StringComparison.Ordinal);
        var command = $"$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', '{pipeName}', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly); $pipe.Connect(5000); $header = New-Object byte[] 4; $offset = 0; while ($offset -lt 4) {{ $read = $pipe.Read($header, $offset, 4 - $offset); if ($read -le 0) {{ exit 2 }}; $offset += $read }}; $length = [BitConverter]::ToInt32($header, 0); $request = New-Object byte[] $length; $offset = 0; while ($offset -lt $length) {{ $read = $pipe.Read($request, $offset, $length - $offset); if ($read -le 0) {{ exit 2 }}; $offset += $read }}; $marker = '{markerLiteral}'; [IO.File]::WriteAllText($marker, '0'); $buffers = [System.Collections.Generic.List[byte[]]]::new(); try {{ for ($i = 0; $i -lt 64; $i++) {{ $buffer = [byte[]]::new(4194304); $buffer[0] = 1; $buffers.Add($buffer); [IO.File]::WriteAllText($marker, [string]($i + 1)) }}; exit 0 }} catch [System.OutOfMemoryException] {{ exit 42 }}";
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the memory-pressure test worker.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                workerExited.TrySetResult(process.ExitCode);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // The process can exit before the exit-code query becomes available.
                workerExited.TrySetResult(int.MinValue);
            }
        };
        if (process.HasExited)
        {
            workerExited.TrySetResult(process.ExitCode);
        }

        workerStarted.TrySetResult(process.Id);
        return process;
    }

    private static Process StartAppHostWorker(
        string pipeName,
        TaskCompletionSource<int> workerStarted,
        TaskCompletionSource<int> workerExited)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "BasitBilgisayarAraclari.App.exe");
        Assert.True(File.Exists(workerPath), $"Expected apphost at {workerPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("--archive-worker");
        startInfo.ArgumentList.Add(pipeName);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the real 7z parser worker.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            try
            {
                workerExited.TrySetResult(process.ExitCode);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                workerExited.TrySetResult(int.MinValue);
            }
        };
        if (process.HasExited)
        {
            workerExited.TrySetResult(process.ExitCode);
        }

        workerStarted.TrySetResult(process.Id);
        return process;
    }

    private static void CreateCompressedSevenZip(string path, long payloadSize)
    {
        using var writer = WriterFactory.OpenWriter(
            path,
            ArchiveType.SevenZip,
            new SevenZipWriterOptions { CompressionType = CompressionType.LZMA2 });
        using var content = new ZeroFillReadStream(payloadSize);
        writer.Write("payload.bin", content, null);
    }

    private static async Task DeleteDirectoryWithRetry(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private static Process StartManifestWorker(
        string executablePath,
        string pipeName,
        string manifestEntryKind,
        TaskCompletionSource<int> workerStarted,
        TaskCompletionSource workerExited)
    {
        var manifestEntry = manifestEntryKind switch
        {
            "traversal" => "@{ relativePath = '../escaped.txt'; payloadName = '00000000000000000000000000000000.payload'; sha256 = ('0' * 64); sizeBytes = 0; order = 0 }",
            "null-entry" => "$null",
            "null-path" => "@{ relativePath = $null; payloadName = '00000000000000000000000000000000.payload'; sha256 = ('0' * 64); sizeBytes = 0; order = 0 }",
            "null-payload-name" => "@{ relativePath = 'payload.txt'; payloadName = $null; sha256 = ('0' * 64); sizeBytes = 0; order = 0 }",
            "null-sha256" => "@{ relativePath = 'payload.txt'; payloadName = '00000000000000000000000000000000.payload'; sha256 = $null; sizeBytes = 0; order = 0 }",
            _ => throw new ArgumentOutOfRangeException(nameof(manifestEntryKind), manifestEntryKind, "Unsupported manifest test entry.")
        };
        var command = $"$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', '{pipeName}', [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly); $pipe.Connect(5000); function Read-Exact([IO.Stream]$stream, [int]$count) {{ $buffer = New-Object byte[] $count; $offset = 0; while ($offset -lt $count) {{ $read = $stream.Read($buffer, $offset, $count - $offset); if ($read -le 0) {{ throw 'Unexpected end of request' }}; $offset += $read }}; return ,$buffer }}; $header = Read-Exact $pipe 4; $length = [BitConverter]::ToInt32($header, 0); [void](Read-Exact $pipe $length); $response = @{{ success = $true; entryCount = 1; files = @({manifestEntry}) }}; $json = ConvertTo-Json -InputObject $response -Compress -Depth 5; $bytes = [Text.Encoding]::UTF8.GetBytes($json); $frame = [BitConverter]::GetBytes([int]$bytes.Length); $pipe.Write($frame, 0, $frame.Length); $pipe.Write($bytes, 0, $bytes.Length); $pipe.Flush(); $pipe.Dispose(); exit 0";
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the manifest test worker.");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => workerExited.TrySetResult();
        if (process.HasExited)
        {
            workerExited.TrySetResult();
        }

        workerStarted.TrySetResult(process.Id);
        return process;
    }

    private static void AssertStagingCleaned(string stagingRoot, string target)
    {
        Assert.True(Directory.Exists(stagingRoot));
        Assert.Empty(Directory.EnumerateDirectories(stagingRoot));
        Assert.Empty(Directory.EnumerateFiles(stagingRoot));
        Assert.False(Directory.Exists(target));
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class ZeroFillReadStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            buffer[..count].Clear();
            _position += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task StopProcessIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (ArgumentException)
        {
            // The child already exited and was reaped before test cleanup.
        }
    }
}
