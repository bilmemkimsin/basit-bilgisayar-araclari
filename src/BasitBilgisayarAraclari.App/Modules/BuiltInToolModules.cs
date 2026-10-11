using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.App.Services.FileOperations;
using BasitBilgisayarAraclari.App.ViewModels;

namespace BasitBilgisayarAraclari.App.Modules;

public sealed class ShutdownToolModule : IToolModule
{
    private readonly ShutdownTimerViewModel _workspace;

    public ShutdownToolModule(IShutdownService? shutdownService = null, TimeProvider? timeProvider = null)
    {
        Descriptor = new ToolDescriptor(
            "shutdown-timer",
            "Zamanlı Kapatma",
            "Saat, dakika ve saniyeyi seç; bilgisayarın ne zaman kapanacağını kolayca planla.",
            "GÜÇ & OTURUM",
            "◷",
            "#EEEAFE",
            "#6657C8",
            "ZAMANLAYICI");

        _workspace = new ShutdownTimerViewModel(
            shutdownService ?? new WindowsShutdownService(),
            timeProvider,
            Descriptor.ToolLabel);
    }

    public ToolDescriptor Descriptor { get; }

    public ToolWorkspaceViewModel CreateWorkspace() => _workspace;
}

public sealed class ExtractMergeToolModule : IToolModule
{
    private readonly FileOperationViewModel _workspace;

    public ExtractMergeToolModule(
        IFileOperationService? fileOperations = null,
        GameProfileStore? profiles = null,
        IFileOperationDialogService? dialogs = null)
    {
        _workspace = new FileOperationViewModel(
            Descriptor,
            FileOperationKind.ExtractAndMerge,
            fileOperations ?? new FileOperationService(),
            profiles ?? new GameProfileStore(),
            dialogs ?? new FileOperationDialogService());
    }

    public ToolDescriptor Descriptor { get; } = new(
        "extract-merge",
        "Genel Çıkar / Birleştir",
        "Arşivleri ve klasörleri tek hedefte birleştir; dosyaların iç yolları korunsun.",
        "DOSYA YÖNETİMİ",
        "⇩",
        "#E5F5F1",
        "#25866F",
        "ARŞİV İŞLEMLERİ");

    public ToolWorkspaceViewModel CreateWorkspace() => _workspace;
}

public sealed class GameModToolModule : IToolModule
{
    private readonly FileOperationViewModel _workspace;

    public GameModToolModule(
        IFileOperationService? fileOperations = null,
        GameProfileStore? profiles = null,
        IFileOperationDialogService? dialogs = null)
    {
        _workspace = new FileOperationViewModel(
            Descriptor,
            FileOperationKind.GameModInstall,
            fileOperations ?? new FileOperationService(),
            profiles ?? new GameProfileStore(),
            dialogs ?? new FileOperationDialogService());
    }

    public ToolDescriptor Descriptor { get; } = new(
        "game-mod-installer",
        "Oyun Modu Yerleştir",
        "Mod arşivlerini ve klasörlerini oyun dizinine ortak yolları koruyarak yerleştir.",
        "OYUN",
        "◇",
        "#FFF2E3",
        "#BD7434",
        "MOD YERLEŞTİRME");

    public ToolWorkspaceViewModel CreateWorkspace() => _workspace;
}
