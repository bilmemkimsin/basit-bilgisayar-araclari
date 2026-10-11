using BasitBilgisayarAraclari.App.Services;
using BasitBilgisayarAraclari.App.Services.FileOperations;

namespace BasitBilgisayarAraclari.App.Modules;

public sealed class ToolRegistry
{
    private readonly IReadOnlyList<IToolModule> _modules;
    private readonly IReadOnlyDictionary<string, IToolModule> _modulesById;

    public ToolRegistry(IEnumerable<IToolModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var moduleList = modules.ToArray();
        var duplicateId = moduleList
            .GroupBy(module => module.Descriptor.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateId is not null)
        {
            throw new ArgumentException($"Birden fazla araç '{duplicateId.Key}' kimliğini kullanıyor.", nameof(modules));
        }

        _modules = Array.AsReadOnly(moduleList);
        _modulesById = moduleList.ToDictionary(module => module.Descriptor.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<IToolModule> Modules => _modules;

    public IToolModule? Find(string id) => _modulesById.GetValueOrDefault(id);

    public static ToolRegistry CreateBuiltIns(
        IShutdownService? shutdownService = null,
        TimeProvider? timeProvider = null,
        IFileOperationService? fileOperationService = null,
        GameProfileStore? gameProfileStore = null,
        IFileOperationDialogService? fileOperationDialogs = null)
    {
        var operations = fileOperationService ?? new FileOperationService();
        var profiles = gameProfileStore ?? new GameProfileStore();
        var dialogs = fileOperationDialogs ?? new FileOperationDialogService();
        return new ToolRegistry(
    [
        new ShutdownToolModule(shutdownService, timeProvider),
        new ExtractMergeToolModule(operations, profiles, dialogs),
        new GameModToolModule(operations, profiles, dialogs)
    ]);
    }
}
