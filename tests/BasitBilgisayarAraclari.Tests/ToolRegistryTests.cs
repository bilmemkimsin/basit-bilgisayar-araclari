using BasitBilgisayarAraclari.App.Modules;

namespace BasitBilgisayarAraclari.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void BuiltInRegistryContainsTheThreePlannedTools()
    {
        var registry = ToolRegistry.CreateBuiltIns();

        Assert.Collection(
            registry.Modules,
            module => Assert.Equal("shutdown-timer", module.Descriptor.Id),
            module => Assert.Equal("extract-merge", module.Descriptor.Id),
            module => Assert.Equal("game-mod-installer", module.Descriptor.Id));
    }

    [Fact]
    public void RegistryFindsToolsByIdAndReturnsNullForUnknownIds()
    {
        var registry = ToolRegistry.CreateBuiltIns();

        Assert.Equal("Zamanlı Kapatma", registry.Find("shutdown-timer")?.Descriptor.Title);
        Assert.Null(registry.Find("missing-tool"));
    }

    [Fact]
    public void RegistryRejectsDuplicateIds()
    {
        var first = new ShutdownToolModule();
        var duplicate = new ShutdownToolModule();

        var error = Assert.Throws<ArgumentException>(() => new ToolRegistry([first, duplicate]));

        Assert.Contains("shutdown-timer", error.Message, StringComparison.Ordinal);
    }
}
