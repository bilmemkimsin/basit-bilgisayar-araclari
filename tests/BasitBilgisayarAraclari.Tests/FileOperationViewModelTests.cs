using System.IO.Compression;
using BasitBilgisayarAraclari.App.Modules;
using BasitBilgisayarAraclari.App.Services.FileOperations;
using BasitBilgisayarAraclari.App.ViewModels;

namespace BasitBilgisayarAraclari.Tests;

public sealed class FileOperationViewModelTests
{
    [Fact]
    public async Task ExtractToolCommands_AddFolderAndArchivePreviewAndApplyToOneTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "BasitBilgisayarAraclari.Tests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "folder-mod");
        var target = Path.Combine(root, "game");
        var archive = Path.Combine(root, "archive-mod.zip");
        var dataRoot = Path.Combine(root, "app-data");
        Directory.CreateDirectory(Path.Combine(folder, "bin", "x64"));
        await File.WriteAllTextAsync(Path.Combine(folder, "bin", "x64", "folder.dll"), "folder-content");
        CreateZip(archive, "bin/x64/archive.dll", "archive-content");

        var dialogs = new TestFileOperationDialogs(archive, folder, target);
        var fileOperations = new FileOperationService(dataRoot);
        using var viewModel = new FileOperationViewModel(
            new ToolDescriptor("extract-test", "Genel Çıkar / Birleştir", "", "", "", "", "", "FAZ 3"),
            FileOperationKind.ExtractAndMerge,
            fileOperations,
            new GameProfileStore(dataRoot),
            dialogs);

        try
        {
            viewModel.AddArchivesCommand.Execute(null);
            viewModel.AddFolderCommand.Execute(null);
            viewModel.PickTargetCommand.Execute(null);

            Assert.Equal(new[] { archive, folder }, viewModel.Sources);
            Assert.Equal(target, viewModel.TargetDirectory);
            Assert.True(viewModel.PreviewCommand.CanExecute(null));

            await viewModel.PreviewCommand.ExecuteAsync();

            Assert.Equal(2, viewModel.PreviewEntries.Count);
            Assert.Contains(viewModel.PreviewEntries, entry => entry.RelativePath == Path.Combine("bin", "x64", "folder.dll"));
            Assert.Contains(viewModel.PreviewEntries, entry => entry.RelativePath == Path.Combine("bin", "x64", "archive.dll"));
            Assert.True(viewModel.ApplyCommand.CanExecute(null));
            Assert.False(Directory.Exists(target));

            await viewModel.ApplyCommand.ExecuteAsync();

            Assert.Equal("folder-content", await File.ReadAllTextAsync(Path.Combine(target, "bin", "x64", "folder.dll")));
            Assert.Equal("archive-content", await File.ReadAllTextAsync(Path.Combine(target, "bin", "x64", "archive.dll")));
            Assert.Empty(viewModel.PreviewEntries);
            Assert.Contains("2 dosya uygulandı", viewModel.StatusMessage);
        }
        finally
        {
            viewModel.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CreateZip(string path, string entryPath, string content)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class TestFileOperationDialogs(string archive, string folder, string target) : IFileOperationDialogService
    {
        public IReadOnlyList<string> PickArchives() => [archive];

        public string? PickFolder(string description) => description == "Kaynak klasörü seç" ? folder : target;

        public string? AskArchivePassword(string archiveName, bool rejectedPreviousPassword) => null;
    }
}
