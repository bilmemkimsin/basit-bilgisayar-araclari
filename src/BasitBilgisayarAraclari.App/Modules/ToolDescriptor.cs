namespace BasitBilgisayarAraclari.App.Modules;

public sealed record ToolDescriptor(
    string Id,
    string Title,
    string Description,
    string Category,
    string IconGlyph,
    string IconBackground,
    string IconForeground,
    string ToolLabel);
