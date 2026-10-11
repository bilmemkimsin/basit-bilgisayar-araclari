namespace BasitBilgisayarAraclari.App.Modules;

public interface IToolModule
{
    ToolDescriptor Descriptor { get; }

    ToolWorkspaceViewModel CreateWorkspace();
}
