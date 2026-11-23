using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.App.Diagnostics;

internal static class DocumentAiRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Archive.DefaultDirectory = Path.Combine(directory, "文稿");
        Directory.CreateDirectory(services.Settings.Archive.DefaultDirectory);
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        SettingsWindow settings = null;
        try
        {
            var ai = services.Modules.GetExport<IAiDocumentService>() ?? throw new InvalidOperationException("AI 存档服务未导出。");
            Check("new feature defaults to off", !services.Settings.Ai.Documents.Enabled);
            Check("AI implementation stays outside the host", !typeof(AppServices).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == "InkCanvas.Modules.AiAssistant"));
            using var model = new LocalVisionModel();
            settings = new SettingsWindow(services, shell: shell); settings.Activate(); settings.SelectCategory("AI 助手");
            await Until(() => Descendants(settings.Content).OfType<TextBox>().Any(box => box.Header?.ToString() == "多模态接口完整地址"));
            var endpoint = Descendants(settings.Content).OfType<TextBox>().Single(box => box.Header?.ToString() == "多模态接口完整地址"); endpoint.Text = model.Endpoint;
            var modelName = Descendants(settings.Content).OfType<TextBox>().Single(box => box.Header?.ToString() == "支持多图输入的模型名称"); modelName.Text = "fixture-vision";
            Invoke(Descendants(settings.Content).OfType<Button>().Single(button => button.Content?.ToString() == "保存模型配置"));
            Check("model settings use the normal WinUI settings editor", services.Store.Load().Ai.Documents.Endpoint == model.Endpoint);
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(settings, theme); await Task.Delay(100);
                await BoardWindowRegression.SaveVisual((FrameworkElement)settings.Content, Path.Combine(directory, $"ai-settings-{theme}.png"));
            }
            settings.Close(); settings = null;
            byte[] background = await BackgroundAsync();
            string small = await ScreenAsync("小屏批注", false), large = await ScreenAsync("大幅批注", true);
            var board = services.Board; board.Background = BoardBackground.White;
            Stroke(board, 120, 100, 650, 420);
            board.InsertPageAfterCurrent(); board.Background = BoardBackground.Grid;
            for (int row = 0; row < 24; row++) Stroke(board, 100, 60 + row * 320, 850, 160 + row * 320);
            board.InsertPageAfterCurrent(); board.Background = BoardBackground.Black;
            Stroke(board, 200, 100, 800, 500);
            string notebook = Path.Combine(services.Settings.Archive.DefaultDirectory, "长页白板.icink");
            await board.SaveBoardAsync(notebook);
            int[] counts = board.Pages.Select(page => page.Document.Count).ToArray();
            var library = services.Modules.GetExport<IBoardLibraryService>();
            var selected = library.PickOpenAsync(new[] { ".icink" });
            await Until(() => shell.OpenWindows.Any(window => window.GetType().Name == "LibraryWindow"));
            var window = shell.OpenWindows.Single(window => window.GetType().Name == "LibraryWindow");
            await Until(() => Descendants(window.Content).OfType<GridView>().Any(grid => grid.Items.Count >= 3));
            var files = Descendants(window.Content).OfType<GridView>().Single();
            files.SelectedItem = files.Items.Cast<object>().Single(item => item.GetType().GetProperty("Path")?.GetValue(item)?.ToString() == notebook);
            string originalHash;
            await using (var input = File.OpenRead(notebook)) originalHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input));
            var describeButton = Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "AI 命名与描述");
            Invoke(describeButton);
            await Until(() => Descendants(window.Content).OfType<TextBox>().Any(input => input.Header?.ToString() == "建议名称" && input.Text.Length > 0), 45000);
            string previewHash;
            await using (var input = File.OpenRead(notebook)) previewHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input));
            Check("AI library suggestion leaves file unchanged until confirmation", originalHash == previewHash && File.Exists(notebook));
            Invoke(Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "取消建议"));
            Check("canceling a naming suggestion keeps original name", File.Exists(notebook));
            Invoke(describeButton);
            await Until(() => Descendants(window.Content).OfType<Border>().Any(border => AutomationProperties.GetName(border) == "AI 命名建议" && border.Visibility == Visibility.Visible), 45000);
            var suggestedTitle = Descendants(window.Content).OfType<TextBox>().Single(input => input.Header?.ToString() == "建议名称");
            var suggestedDescription = Descendants(window.Content).OfType<TextBox>().Single(input => input.Header?.ToString() == "内容摘要");
            suggestedTitle.Text = "确认后的课堂文稿"; suggestedDescription.Text = "教师编辑的课堂摘要";
            Invoke(Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "应用"));
            string confirmed = Path.Combine(services.Settings.Archive.DefaultDirectory, "确认后的课堂文稿.icink");
            await Until(() => File.Exists(confirmed) && files.Items.Cast<object>().Any(item => item.GetType().GetProperty("Path")?.GetValue(item)?.ToString() == confirmed), 30000);
            Check("applying edited AI suggestion updates name and embedded description", (await BoardService.ReadArchiveMetadataAsync(confirmed))?.Description == "教师编辑的课堂摘要");
            notebook = confirmed;
            var openButton = Descendants(window.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "打开文稿");
            await Until(() => openButton.IsEnabled);
            Invoke(openButton);
            await selected;
            int start = model.ImageCount;
            var smallResult = await ai.DescribeAsync(new(small));
            Check("ordinary annotation PNG preserves original dimensions", model.Images.Skip(start).Any(image => image.Width == 512 && image.Height == 288));
            var largeResult = await ai.DescribeAsync(new(large));
            Check("large annotation sends an overview and HD details", largeResult.Images >= 2 && model.Images.Any(image => image.Label.Contains("overview")) && model.Images.Any(image => image.Label.Contains("detail")));
            int calls = model.Calls;
            var result = await ai.DescribeAsync(new(notebook));
            Check("all notebook pages and long segments reach the model", result.Images > 6 && model.Calls - calls >= 2 && model.SummaryCarried);
            var restored = new BoardService();
            try
            {
                await restored.LoadBoardAsync(result.Path);
                Check("renamed archive retains every editable page", restored.PageCount == 3 && restored.Pages.Select(page => page.Document.Count).SequenceEqual(counts));
                Check("model title and description persist inside the archive", restored.ArchiveMetadata.Title == "AI课堂笔记" && restored.ArchiveMetadata.Description.Contains("本地多图验收"));
            }
            finally { foreach (var page in restored.Pages) page.Dispose(); }
            await Until(() => File.ReadAllText(Path.Combine(services.Store.Directory, "PluginData", "inkcanvas.library", "library.json")).Contains("AI"));
            using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(services.Store.Directory, "PluginData", "inkcanvas.library", "library.json"))))
                Check("closed library recent list follows the renamed archive", state.RootElement.GetProperty("Recent").EnumerateArray().Any(value => value.GetString() == result.Path));
            string failure = Path.Combine(services.Settings.Archive.DefaultDirectory, "模型故障.icink"); await board.SaveBoardAsync(failure);
            model.FailNext = true;
            try { await ai.DescribeAsync(new(failure)); Check("model failure is reported", false); }
            catch (IOException) { Check("model failure preserves original file", File.Exists(failure) && (await BoardService.ReadArchiveMetadataAsync(failure))?.Description != "本地多图验收"); }
            settings = new SettingsWindow(services, shell: shell); settings.Activate(); settings.SelectCategory("AI 助手");
            await Until(() => Descendants(settings.Content).OfType<ToggleSwitch>().Any(toggle => toggle.Header?.ToString() == "保存后自动分析并命名"));
            var enabled = Descendants(settings.Content).OfType<ToggleSwitch>().Single(toggle => toggle.Header?.ToString() == "保存后自动分析并命名");
            enabled.IsOn = true;
            string automatic = Path.Combine(services.Settings.Archive.DefaultDirectory, "保存后处理.icink");
            await services.Archives.SaveManualAsync(board, automatic, true);
            await Until(() => !File.Exists(automatic) && services.Archives.LastSave?.Path != automatic, 45000);
            Check("normal save completion automatically names the file and updates history", File.Exists(services.Archives.LastSave.Path));
            enabled.IsOn = false;
            int before = model.Calls;
            string off = Path.Combine(services.Settings.Archive.DefaultDirectory, "关闭功能.icink"); await services.Archives.SaveManualAsync(board, off, true);
            await Task.Delay(400);
            Check("turning the feature off stops future model requests", model.Calls == before && File.Exists(off));
            Check("plugin adapters are available to the shared host", shell.CanvasToolsTestHost.DocumentAi?.Available == true);
            var pdf = services.Modules.GetExport<IPdfWorkspaceService>();
            string pdfPath = Path.Combine(directory, "pdf-adapter.pdf");
            pdf.GetType().Assembly.GetType("InkCanvas.Modules.Pdf.PdfDiagnostics", true)
                .GetMethod("CreateFixture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { pdfPath, 2 });
            await pdf.OpenAsync(pdfPath);
            var workspace = shell.OpenWindows.OfType<IDocumentWorkspace>().Single();
            enabled.IsOn = true;
            await workspace.SaveAsync();
            await Until(() => File.Exists(pdfPath + ".ai.json"), 30000);
            Check("PDF plugin supplies native pages after normal save", File.ReadAllText(pdfPath + ".ai.json").Contains("AI"));
            enabled.IsOn = false;
            await workspace.CloseAsync();
            var geometry = services.Modules.GetExport<IAnalyticGeometryService>(); geometry.Open();
            var geometryWindow = shell.OpenWindows.Single(window => window.GetType().Name == "GeometryWorkbenchWindow");
            var document = geometryWindow.GetType().GetProperty("Document").GetValue(geometryWindow);
            var snapshot = document.GetType().GetProperty("State").GetValue(document);
            string geometryPath = Path.Combine(directory, "geometry-adapter.icgeo");
            var saveGeometry = document.GetType().GetMethod("SaveAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            await (Task)saveGeometry.Invoke(null, new object[] { geometryPath, snapshot, CancellationToken.None });
            var export = geometryWindow.GetType().GetMethod("ExportAiGeometryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var geometryResult = await ai.DescribeAsync(new(geometryPath, "inkcanvas.analytic-geometry")
            { ExportImages = (folder, token) => (IAsyncEnumerable<AiDocumentImage>)export.Invoke(null, new object[] { snapshot, folder, token }) });
            Check("geometry plugin uses its native vector renderer through the AI adapter", geometryResult.Images == 1 && File.Exists(geometryPath + ".ai.json"));
            geometryWindow.Close();
            checks.Add(new { check = "local fixture transport metrics", passed = true, requests = model.Calls, images = model.ImageCount });

            async Task<string> ScreenAsync(string name, bool huge)
            {
                var screen = new BoardService { Background = BoardBackground.White };
                try
                {
                    screen.AddImage(new() { Data = background, Width = 512, Height = 288, LayerOrder = 0, Metadata = """{"Kind":"screen-capture-background","DpiScale":1}""" });
                    Stroke(screen, 50, 70, 350, 190);
                    if (huge) for (int index = 0; index < 32; index++) Stroke(screen, 7000, 3000 + index * 20, 9200, 3200 + index * 20);
                    string path = Path.Combine(services.Settings.Archive.DefaultDirectory, name + ".icink");
                    await screen.SaveBoardArchiveAsync(path, new() { Kind = "screen", PageCount = 1, Title = name }); return path;
                }
                finally { foreach (var page in screen.Pages) page.Dispose(); }
            }
        }
        catch (Exception ex) { checks.Add(new { check = "AI document regression exception", passed = false, detail = ex.ToString() }); }
        finally
        {
            settings?.Close();
            services.Settings.Ai.Documents.Enabled = false;
            foreach (var window in shell.OpenWindows.Where(window => window.GetType().Name == "LibraryWindow").ToArray()) window.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "document-ai.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool passed) => checks.Add(new { check, passed });
    }


    private static void Stroke(BoardService board, float x, float y, float endX, float endY)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 5, TaperKind = TaperKind.InkWash, Color = InkColor.FromPackedArgb(0xFFCC3030) });
        stroke.AddPoint(x, y); stroke.AddPoint(endX, endY); stroke.Complete(); board.Current.Document.Add(stroke);
    }
    private static async Task<byte[]> BackgroundAsync()
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var bytes = Enumerable.Repeat((byte)255, 512 * 288 * 4).ToArray();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 512, 288, 96, 96, bytes); await encoder.FlushAsync();
        stream.Seek(0); using var reader = new DataReader(stream); await reader.LoadAsync((uint)stream.Size);
        var png = new byte[stream.Size]; reader.ReadBytes(png); return png;
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task Until(Func<bool> condition, int timeout = 15000)
    {
        long start = Environment.TickCount64;
        while (!condition()) { if (Environment.TickCount64 - start > timeout) throw new TimeoutException("AI 存档验收超时。"); await Task.Delay(40); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed record ModelImage(string Label, int Width, int Height);
    private sealed class LocalVisionModel : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<ModelImage> _images = new();
        private int _calls;
        public bool FailNext, SummaryCarried;
        public string Endpoint { get; }
        public int Calls => Volatile.Read(ref _calls);
        public int ImageCount { get { lock (_images) return _images.Count; } }
        public ModelImage[] Images { get { lock (_images) return _images.ToArray(); } }
        public LocalVisionModel()
        {
            var available = new TcpListener(IPAddress.Loopback, 0); available.Start(); int port = ((IPEndPoint)available.LocalEndpoint).Port; available.Stop();
            string prefix = $"http://127.0.0.1:{port}/"; Endpoint = prefix + "v1/chat/completions";
            _listener.Prefixes.Add(prefix); _listener.Start(); _ = RunAsync();
        }
        private async Task RunAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _calls);
                    using var payload = await JsonDocument.ParseAsync(context.Request.InputStream);
                    var content = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
                    string label = "";
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.GetProperty("type").GetString() == "text")
                        { label = part.GetProperty("text").GetString(); if (label.Contains("前面批次的内容摘要")) SummaryCarried = true; }
                        else
                        {
                            string data = part.GetProperty("image_url").GetProperty("url").GetString();
                            byte[] png = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
                            lock (_images) _images.Add(new(label, (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4))));
                        }
                    }
                    if (FailNext) { FailNext = false; context.Response.StatusCode = 503; context.Response.Close(); continue; }
                    string answer = JsonSerializer.Serialize(new { title = "AI课堂笔记", description = "本地多图验收，包含函数图像、板书与长页课堂笔记。" });
                    byte[] response = JsonSerializer.SerializeToUtf8Bytes(new { choices = new[] { new { message = new { content = answer } } } });
                    context.Response.ContentType = "application/json"; context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
                }
                catch (Exception) when (!_listener.IsListening) { return; }
            }
        }
        public void Dispose() => _listener.Close();
    }
}
