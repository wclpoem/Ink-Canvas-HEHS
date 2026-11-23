using InkCanvas.Features.Board;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// v3 / v2 画板包迁移回归：旧包缺少 PageId、TextObjects、FormulaObjects 等 v4 字段，
/// 加载阶段必须按缺省值补齐并在运行期生成页身份，保存时无条件写回 v4。
/// 无 ZIP 签名的 legacy 单页 JSON 同样应可读取、生成页身份并写回 v4。
/// 全部使用临时目录，不依赖 GPU / UI / 网络。
/// </summary>
public sealed class BoardLegacyPackageMigrationTests : IDisposable
{
    private const string LegacyBoardJson = """
        {
          "Version": 1,
          "Strokes": [
            { "Width": 7, "Points": [ [1, 2, 0.5, 5, 0], [3, 4, 0.5, 5, 10] ] }
          ]
        }
        """;

    private readonly string _directory =
        Directory.CreateTempSubdirectory("inkcanvas-legacy-package-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* 临时目录清理失败不影响测试结论。 */ }
    }

    private static string LegacyPackageJson(int version) => """
        {
          "Format": "inkcanvas-board",
          "Version": __VERSION__,
          "Background": "Grid",
          "GridSpacing": 56,
          "Pages": [
            {
              "Name": "旧页 1",
              "Background": "Lined",
              "GridSpacing": 24,
              "Strokes": [ { "Width": 5, "Points": [ [12, 34, 0.5, 5, 0] ] } ],
              "Images": [],
              "Media": []
            },
            {
              "Name": "旧页 2",
              "Strokes": [],
              "Images": [],
              "Media": []
            }
          ]
        }
        """.Replace("__VERSION__", version.ToString());

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static void WritePackage(string path, string boardJson)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(
            archive.CreateEntry("board.json").Open(), new UTF8Encoding(false));
        writer.Write(boardJson);
    }

    private static void DisposePages(BoardService board)
    {
        foreach (var page in board.Pages) page.Dispose();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyPackageWithoutV4Fields_LoadsDefaultsGeneratesPageIdsAndSavesV4(int version)
    {
        string source = PathFor($"legacy-v{version}.icink");
        WritePackage(source, LegacyPackageJson(version));

        // 旧包缺 PageId/TextObjects/FormulaObjects：加载不得抛异常。
        var board = new BoardService();
        try
        {
            await board.LoadBoardAsync(source);

            Assert.Equal(2, board.PageCount);
            Guid firstId = board.ResolvePageId(0);
            Guid secondId = board.ResolvePageId(1);
            Assert.NotEqual(Guid.Empty, firstId);
            Assert.NotEqual(Guid.Empty, secondId);
            Assert.NotEqual(firstId, secondId);
            Assert.Equal(firstId, board.CurrentPageId);

            // 缺失的对象字段按空集合处理，旧页的墨迹与纸张信息保持。
            Assert.Empty(board.CurrentTextObjects);
            Assert.Empty(board.CurrentFormulaObjects);
            Assert.Equal(BoardBackground.Lined, board.Background);
            Assert.Equal(24, board.GridSpacing);
            Assert.Single(board.Current.Document.Strokes);
            Assert.Equal(5, board.Current.Document.Strokes[0].Attributes.BaseWidth, 3);

            string saved = PathFor($"migrated-v{version}.icink");
            await board.SaveBoardAsync(saved);

            using ZipArchive archive = ZipFile.OpenRead(saved);
            using Stream manifest = archive.GetEntry("board.json")!.Open();
            using JsonDocument document = JsonDocument.Parse(manifest);
            JsonElement root = document.RootElement;

            Assert.Equal(4, root.GetProperty("Version").GetInt32());
            JsonElement pages = root.GetProperty("Pages");
            Assert.Equal(2, pages.GetArrayLength());

            Assert.True(pages[0].TryGetProperty("PageId", out JsonElement firstSavedId));
            Assert.Equal(firstId, firstSavedId.GetGuid());
            Assert.True(pages[1].TryGetProperty("PageId", out JsonElement secondSavedId));
            Assert.Equal(secondId, secondSavedId.GetGuid());

            Assert.True(pages[0].TryGetProperty("TextObjects", out JsonElement texts));
            Assert.Equal(0, texts.GetArrayLength());
            Assert.True(pages[0].TryGetProperty("FormulaObjects", out JsonElement formulas));
            Assert.Equal(0, formulas.GetArrayLength());
        }
        finally
        {
            DisposePages(board);
        }
    }

    [Fact]
    public async Task LegacySinglePageJsonWithoutZipSignature_LoadsGeneratesPageIdAndSavesV4()
    {
        string source = PathFor("legacy-board.icink");
        await File.WriteAllTextAsync(source, LegacyBoardJson);

        var board = new BoardService();
        try
        {
            await board.LoadBoardAsync(source);

            Assert.Equal(1, board.PageCount);
            Assert.Single(board.Current.Document.Strokes);
            Assert.Empty(board.CurrentTextObjects);
            Assert.Empty(board.CurrentFormulaObjects);
            Guid pageId = board.CurrentPageId;
            Assert.NotEqual(Guid.Empty, pageId);

            string saved = PathFor("legacy-board-resaved.icink");
            await board.SaveBoardAsync(saved);

            Assert.Equal(new[] { 'P', 'K' }, File.ReadAllBytes(saved).Take(2).Select(value => (char)value));

            using ZipArchive archive = ZipFile.OpenRead(saved);
            using Stream manifest = archive.GetEntry("board.json")!.Open();
            using JsonDocument document = JsonDocument.Parse(manifest);
            JsonElement root = document.RootElement;

            Assert.Equal(4, root.GetProperty("Version").GetInt32());
            JsonElement page = root.GetProperty("Pages")[0];
            Assert.Equal(pageId, page.GetProperty("PageId").GetGuid());
            Assert.Single(page.GetProperty("Strokes").EnumerateArray());
        }
        finally
        {
            DisposePages(board);
        }
    }
}