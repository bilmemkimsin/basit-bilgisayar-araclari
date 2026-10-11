namespace BasitBilgisayarAraclari.App.Services.FileOperations;

public interface IFileOperationDialogService
{
    IReadOnlyList<string> PickArchives();

    string? PickFolder(string description);

    string? AskArchivePassword(string archiveName, bool rejectedPreviousPassword);
}
