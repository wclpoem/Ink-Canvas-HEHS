using System.IO.Compression;
using System.Text;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class BoardExtensionTests
{
    [Fact]
    public async Task OpaquePluginDataSurvivesBoardAndPageSavesWithoutPlugin()
    {
        string directory = Directory.CreateTempSubdirectory("InkCanvas-extension-test-").FullName;
        var board = new BoardService(); var restored = new BoardService();
        try
        {
            string path = Path.Combine(directory, "board.icink"), copy = Path.Combine(directory, "copy.icink");
            byte[] payload = Encoding.UTF8.GetBytes("{\"Version\":1,\"Chats\":[\"teacher conversation\"]}");
            board.WriteExtension("inkcanvas.ai-assistant", payload); Assert.True(board.HasContent);
            await board.SaveBoardAsync(path);
            using (var archive = ZipFile.OpenRead(path))
            {
                Assert.NotNull(archive.GetEntry("board.json"));
                Assert.NotNull(archive.GetEntry("extensions/inkcanvas.ai-assistant.bin"));
            }
            Guid previous = restored.DocumentId; await restored.LoadBoardAsync(path);
            Assert.NotEqual(previous, restored.DocumentId); Assert.Equal(payload, restored.ReadExtension("inkcanvas.ai-assistant"));
            await restored.SavePageAsync(copy); await board.LoadPageAsync(copy);
            Assert.Equal(payload, board.ReadExtension("inkcanvas.ai-assistant"));
            var detached = board.ReadExtension("inkcanvas.ai-assistant"); detached[0] = 0;
            Assert.Equal(payload, board.ReadExtension("inkcanvas.ai-assistant"));
        }
        finally
        {
            foreach (var page in board.Pages.Concat(restored.Pages)) page.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
