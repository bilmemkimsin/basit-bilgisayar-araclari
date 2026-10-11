namespace BasitBilgisayarAraclari.App.Services;

public interface IProcessRunner
{
    Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments);
}
