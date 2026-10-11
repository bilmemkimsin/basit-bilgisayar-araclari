using System.Windows;
using System.Windows.Controls;
using BasitBilgisayarAraclari.App.Localization;
using WpfApplication = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOrientation = System.Windows.Controls.Orientation;
using Forms = System.Windows.Forms;

namespace BasitBilgisayarAraclari.App.Services.FileOperations;

public sealed class FileOperationDialogService : IFileOperationDialogService
{
    public IReadOnlyList<string> PickArchives()
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = LocalizationManager.Instance.Translate("Arşiv dosyalarını seç"),
            Filter = LocalizationManager.Instance.Translate("Desteklenen arşivler|*.zip;*.7z;*.rar;*.tar;*.gz;*.bz2;*.xz|Tüm dosyalar|*.*"),
            Multiselect = true,
            CheckFileExists = true
        };

        return dialog.ShowDialog(WpfApplication.Current?.MainWindow) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string description)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationManager.Instance.TranslateUserMessage(description),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? AskArchivePassword(string archiveName, bool rejectedPreviousPassword)
    {
        var passwordBox = new PasswordBox { MinWidth = 320, Padding = new Thickness(9), Margin = new Thickness(0, 8, 0, 12) };
        var sourceMessage = rejectedPreviousPassword
            ? "Önceki parola kabul edilmedi. Parolayı tekrar gir."
            : "Bu arşiv parolalı. Parola yalnızca bu işlem boyunca bellekte tutulur.";
        var message = LocalizationManager.Instance.Translate(sourceMessage);
        var accepted = false;
        var window = new Window
        {
            Title = LocalizationManager.Instance.TranslateUserMessage($"Arşiv parolası — {archiveName}"),
            Width = 420,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = WpfApplication.Current?.MainWindow,
            Content = BuildPasswordContent(message, passwordBox, value => accepted = value)
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");

        window.ShowDialog();
        return accepted ? passwordBox.Password : null;
    }

    private static UIElement BuildPasswordContent(string message, PasswordBox passwordBox, Action<bool> setAccepted)
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        var messageBlock = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        messageBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(messageBlock);
        panel.Children.Add(passwordBox);
        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = WpfHorizontalAlignment.Right };
        var cancel = new WpfButton { Content = LocalizationManager.Instance.Translate("İptal"), MinWidth = 82, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var accept = new WpfButton { Content = LocalizationManager.Instance.Translate("Devam"), MinWidth = 82, Padding = new Thickness(10, 6, 10, 6), IsDefault = true };
        accept.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(passwordBox.Password))
            {
                return;
            }

            setAccepted(true);
            Window.GetWindow(accept)?.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        var border = new Border { CornerRadius = new CornerRadius(14), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return border;
    }
}
