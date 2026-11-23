using InkCanvas.Features.Board;
using InkCanvas.Features.Ppt;
using System.Buffers.Binary;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 讲义导出追加白板页：图片与注入的板书渲染结果逐字节一致（测试环境不依赖 GPU），
/// PDF 页数 = PPT 页数 + 板书页数；无白板内容时保持原有 PPT 讲义结构。
/// </summary>
public sealed class BoardHandoutExportTests
{
    // 有效的 1×1 PNG；BoardPageRenderer 用真实 PNG 解码路径绘制 AI 卡片。
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const int PageWidth = 1240;

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-board-handout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响断言 */ }
    }

    private static SlideContentIndex PptIndex() => new()
    {
        Version = 1,
        Slides = new List<SlideContentEntry>
        {
            new() { SlideId = 1, Index = 1, Name = "第一页" },
            new() { SlideId = 2, Index = 2, Name = "第二页" },
        },
    };

    private static HandoutOptions Options() => new()
    {
        Content = HandoutContentMode.AnnotationsWithTeacherNotes,
        SelectedSlideIds = new List<int> { 1, 2 },
    };

    private static BoardService BoardWithAiCard()
    {
        var board = new BoardService();
        var card = new BoardImageAsset
        {
            Name = "AI 板书·文本",
            Data = Convert.FromBase64String(OnePixelPngBase64),
            ContentType = "image/png",
            Left = 24,
            Top = 32,
            Width = 240,
            Height = 120,
        };
        Assert.Single(board.AddImagesAtomic(new[] { card }, "AI 板书"));
        return board;
    }

    [Fact]
    public async Task Export_AppendsBoardPageAndMatchesRendererOutput()
    {
        string dir = TempDir();
        try
        {
            BoardService board = BoardWithAiCard();
            const int boardWidth = 576;
            const int boardHeight = 336;
            byte[] injectedPng = await CreateDeterministicPngAsync(boardWidth, boardHeight);
            var request = new HandoutRenderRequest
            {
                Index = PptIndex(),
                Options = Options(),
                Board = board,
                PageWidthPx = PageWidth,
                BoardPageRender = (_, _, _, _) => Task.FromResult(new BoardPageRenderResult
                {
                    Png = injectedPng,
                    WidthPx = boardWidth,
                    HeightPx = boardHeight,
                }),
            };

            HandoutExportResult result = await HandoutExportService.ExportAsync(request, dir);

            Assert.True(result.Success, string.Join("；", result.Warnings));
            Assert.Equal(3, result.Pages.Count);
            HandoutPageResult boardPage = Assert.Single(result.Pages, page => page.SlideId < 0);
            Assert.Equal(3, boardPage.Index);
            Assert.True(File.Exists(boardPage.PageImagePath), "板书讲义 PNG 不存在。");

            byte[] handoutPng = await File.ReadAllBytesAsync(boardPage.PageImagePath);
            Assert.NotEmpty(handoutPng);
            Assert.Equal(injectedPng, handoutPng);
            Assert.Equal(boardWidth, PngWidth(handoutPng));
            Assert.Equal(boardHeight, PngHeight(handoutPng));

            Assert.False(string.IsNullOrEmpty(result.PdfPath));
            Assert.Equal(3, CountPdfPages(result.PdfPath));
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public async Task Export_WithoutBoardContent_DoesNotAppendBoardPage()
    {
        string dir = TempDir();
        try
        {
            var request = new HandoutRenderRequest
            {
                Index = PptIndex(),
                Options = Options(),
                Board = new BoardService(),
                PageWidthPx = PageWidth,
            };

            HandoutExportResult result = await HandoutExportService.ExportAsync(request, dir);

            Assert.True(result.Success, string.Join("；", result.Warnings));
            Assert.Equal(2, result.Pages.Count);
            Assert.DoesNotContain(result.Pages, page => page.SlideId < 0);
            Assert.False(string.IsNullOrEmpty(result.PdfPath));
            Assert.Equal(2, CountPdfPages(result.PdfPath));
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    /// <summary>
    /// 无 PPT（空课件索引）时仅导出板书页：PDF 1 页、PNG 非空且与渲染注入逐字节一致，
    /// 导出过程不改写源白板文件。
    /// </summary>
    [Fact]
    public async Task Export_WithoutPresentation_ExportsOnlyBoardPageAndKeepsSourceFileUnchanged()
    {
        string dir = TempDir();
        try
        {
            string sourcePath = Path.Combine(dir, "board.icink");
            var authored = BoardWithAiCard();
            await authored.SaveBoardAsync(sourcePath);
            byte[] sourceBytes = await File.ReadAllBytesAsync(sourcePath);

            var board = new BoardService();
            await board.LoadBoardAsync(sourcePath);

            const int boardWidth = 640;
            const int boardHeight = 360;
            byte[] injectedPng = await CreateDeterministicPngAsync(boardWidth, boardHeight);
            var request = new HandoutRenderRequest
            {
                Index = new SlideContentIndex { Version = 1, Slides = new List<SlideContentEntry>() },
                Options = new HandoutOptions
                {
                    Content = HandoutContentMode.AnnotationsWithTeacherNotes,
                    SelectedSlideIds = new List<int>(),
                },
                Board = board,
                PageWidthPx = PageWidth,
                BoardPageRender = (_, _, _, _) => Task.FromResult(new BoardPageRenderResult
                {
                    Png = injectedPng,
                    WidthPx = boardWidth,
                    HeightPx = boardHeight,
                }),
            };

            HandoutExportResult result = await HandoutExportService.ExportAsync(request, dir);

            Assert.True(result.Success, string.Join("；", result.Warnings));
            HandoutPageResult boardPage = Assert.Single(result.Pages);
            Assert.True(boardPage.SlideId < 0, "无 PPT 时唯一页面必须是板书页。");
            Assert.Equal(1, boardPage.Index);
            Assert.True(File.Exists(boardPage.PageImagePath), "板书讲义 PNG 不存在。");

            byte[] handoutPng = await File.ReadAllBytesAsync(boardPage.PageImagePath);
            Assert.NotEmpty(handoutPng);
            Assert.Equal(injectedPng, handoutPng);
            Assert.Equal(boardWidth, PngWidth(handoutPng));
            Assert.Equal(boardHeight, PngHeight(handoutPng));

            Assert.False(string.IsNullOrEmpty(result.PdfPath));
            Assert.Equal(1, CountPdfPages(result.PdfPath));

            // 原白板文件在导出前后逐字节一致。
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    private static async Task<byte[]> CreateDeterministicPngAsync(int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x30;
            pixels[index + 1] = 0x80;
            pixels[index + 2] = 0xD0;
            pixels[index + 3] = 0xFF;
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static int PngWidth(byte[] png)
    {
        Assert.True(png.Length >= 24, "PNG 长度不足。");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        return BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
    }

    private static int PngHeight(byte[] png)
    {
        Assert.True(png.Length >= 24, "PNG 长度不足。");
        return BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
    }

    private static int CountPdfPages(string pdfPath)
    {
        Assert.True(File.Exists(pdfPath), "PDF 不存在：" + pdfPath);
        string text = Encoding.ASCII.GetString(File.ReadAllBytes(pdfPath));
        // HandoutPdfBuilder 每页写 `/Type /Page /Parent`，/Pages 节点不会命中该片段。
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf("/Type /Page /Parent", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += "/Type /Page /Parent".Length;
        }
        return count;
    }
}