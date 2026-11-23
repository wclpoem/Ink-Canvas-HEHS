using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

public interface IBoardLibraryHost
{
    string DefaultDirectory { get; }
    string DataDirectory { get; }
    void ConfigureWindow(Window window);
    Task<bool> OpenBoardAsync(string path);
    IAiDocumentBridge DocumentAi => null;
}

public interface IBoardLibraryService
{
    void Relocate(string previous, string path) { }
    void Open();
    Task<string> PickOpenAsync(IReadOnlyList<string> extensions);
    Task<string> PickSaveAsync(string suggestedName);
}
