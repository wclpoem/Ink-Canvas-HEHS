using InkCanvas.Extensibility;
using InkCanvas.Host.Contracts;
using InkCanvas.Pdf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.Pdf;

public sealed class PdfModule : IApplicationModule
{
    private PdfWorkspaceService _service;
    public void Start(IModuleContext context)
    {
        _service = new PdfWorkspaceService(context.GetService<IDocumentWorkspaceHost>());
        context.RegisterCleanup(_service.Dispose);
        context.Export<IPdfWorkspaceService>(_service);
        _service.RegisterAssistant(context);
    }
    public void Stop() { _service?.Dispose(); _service = null; }
}

internal sealed partial class PdfWorkspaceService(IDocumentWorkspaceHost host) : IPdfWorkspaceService, IDisposable
{
    private PdfWindow _window;
    private bool _busy, _disposed;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _operation;
    private Window _operationWindow;

    public Task OpenAsync(string path = null) => OpenCoreAsync(path, CancellationToken.None, reportErrors: true);
    internal Task OpenForAssistantAsync(string path, CancellationToken token) => OpenCoreAsync(path, token, reportErrors: false);

    private async Task OpenCoreAsync(string path, CancellationToken token, bool reportErrors)
    {
        if (_disposed || _busy) return;
        _busy = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        PdfAnnotationFile file = null; PdfRasterizer renderer = null;
        try
        {
            path ??= await PickAsync();
            if (path is null || _disposed) return;
            if (_window is not null && Path.GetFullPath(path).Equals(_window.FilePath, StringComparison.OrdinalIgnoreCase))
            { _window.Activate(); return; }
            await ShowOperationWindowAsync("正在打开 PDF…");
            file = await OpenFileAsync(path);
            if (file is null) return;
            renderer = await PdfRasterizer.OpenAsync(file, _operation.Token);
            if (_disposed) return;
            CloseOperationWindow();
            if (_window is not null && !await _window.CloseAsync()) return;
            _operation.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            PdfWindow window = null;
            window = new PdfWindow(host, file, renderer, () => OpenAsync(), () => ImportToBoardAsync(window.FilePath));
            file = null; renderer = null;
            _window = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
            host.Attach(window);
            window.Activate();
        }
        catch (OperationCanceledException) when (reportErrors) { }
        catch (Exception ex) when (reportErrors) { if (!_disposed) await NoticeAsync("打开 PDF 失败", ex.GetBaseException().Message); }
        finally
        {
            if (renderer is not null) await renderer.DisposeAsync();
            if (file is not null) await Task.Run(file.Dispose);
            CloseOperationWindow(); _operation.Dispose(); _operation = null;
            _busy = false;
        }
    }
    private async Task<string> PickAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(host.Owner));
        var selected = await picker.PickSingleFileAsync(); return selected?.Path;
    }
    private async Task<PdfAnnotationFile> OpenFileAsync(string path)
    {
        string password = null;
        while (!_disposed)
        {
            try { return await PdfAnnotationFile.OpenAsync(path, password, _operation.Token); }
            catch (PdfPasswordRequiredException)
            {
                var input = new PasswordBox { PlaceholderText = "PDF 打开密码", MinWidth = 280, MinHeight = 44 };
                var dialog = new ContentDialog { Title = password is null ? "此 PDF 需要密码" : "密码不正确，请重试", Content = input,
                    PrimaryButtonText = "打开", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = Root() };
                input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
                if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;
                password = input.Password;
            }
        }
        return null;
    }
    private XamlRoot Root() => ((_operationWindow ?? host.Owner).Content as FrameworkElement)?.XamlRoot ?? throw new InvalidOperationException("PDF 窗口尚未就绪，请重试。");
    private async Task ShowOperationWindowAsync(string title)
    {
        var cancellation = _operation;
        var cancel = new Button { Content = "取消", MinHeight = 44, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => cancellation.Cancel();
        var content = new StackPanel { Spacing = 20, Padding = new Thickness(24), VerticalAlignment = VerticalAlignment.Center,
            Children = { new ProgressRing { IsActive = true, Width = 36, Height = 36 }, new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center }, cancel } };
        var window = new Window { Title = "Ink Canvas PDF", Content = content };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.Loaded += (_, _) => loaded.TrySetResult();
        _operationWindow = window;
        host.ConfigureWindow(window, 520, 300);
        window.Closed += (_, _) => { if (ReferenceEquals(_operationWindow, window)) { _operationWindow = null; cancellation.Cancel(); } };
        window.Activate();
        await loaded.Task.WaitAsync(cancellation.Token);
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        try { return await dialog.ShowAsync().AsTask(_operation.Token); }
        finally { if (_operation.IsCancellationRequested) dialog.Hide(); }
    }
    private void CloseOperationWindow()
    {
        var window = _operationWindow; _operationWindow = null; window?.Close();
    }
    private async Task NoticeAsync(string title, string text)
    {
        if (_disposed) return;
        try { await ShowDialogAsync(new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "关闭", XamlRoot = Root() }); }
        catch (Exception ex) { Core.Diagnostics.FileLog.WriteLogToFile("PDF notice " + text + " " + ex.Message, Core.Diagnostics.LogType.Error); }
    }

    public async Task ImportToBoardAsync(string path = null)
    {
        if (_disposed || _busy) return;
        _busy = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        PdfAnnotationFile file = null; PdfRasterizer renderer = null;
        string staging = Path.Combine(Path.GetTempPath(), "InkCanvas-Pdf-Import", Guid.NewGuid().ToString("N"));
        try
        {
            path ??= await PickAsync(); if (path is null) return;
            // Save current editable ink first so an import from the open workspace includes it.
            if (_window is not null && Path.GetFullPath(path).Equals(_window.FilePath, StringComparison.OrdinalIgnoreCase)
                && _window.IsDirty && !await _window.SaveAsync()) return;
            await ShowOperationWindowAsync("正在准备 PDF 导入…");
            file = await OpenFileAsync(path); if (file is null) return;
            var range = new TextBox { Text = "1-" + file.Pages.Count, PlaceholderText = "例如 1-5,8,10", MinWidth = 280, MinHeight = 44 };
            var quality = new ComboBox { ItemsSource = new[] { "标准 · 适合课堂书写", "高清 · 适合放大查看" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed) };
            var panel = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "PDF 共 " + file.Pages.Count + " 页，请选择导入范围" }, range, quality, error } };
            IReadOnlyList<int> indices = null;
            var dialog = new ContentDialog { Title = "导入 PDF 到新画板页", Content = panel, PrimaryButtonText = "导入", CloseButtonText = "取消", XamlRoot = Root() };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { indices = PdfPageRange.Parse(range.Text, file.Pages.Count, file.Pages.Count); }
                catch (FormatException ex) { args.Cancel = true; error.Text = ex.Message; }
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || indices is null) return;
            renderer = await PdfRasterizer.OpenAsync(file, _operation.Token);
            Directory.CreateDirectory(staging);
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token);
            var progress = new ProgressBar { IsIndeterminate = false, Minimum = 0, Maximum = indices.Count, Value = 0 };
            var label = new TextBlock { Text = "正在准备页面…" };
            var progressDialog = new ContentDialog { Title = "导入 PDF", Content = new StackPanel { Spacing = 12, Children = { label, progress } },
                CloseButtonText = "取消", XamlRoot = Root() };
            bool complete = false;
            progressDialog.Closing += (_, _) => { if (!complete) cancel.Cancel(); };
            var shown = progressDialog.ShowAsync();
            var prepared = new List<(int Index, string Path)>(); long totalBytes = 0;
            try
            {
                foreach (int index in indices)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    var page = file.Pages[index]; double scale = quality.SelectedIndex == 1 ? 2 : 1.5;
                    scale = Math.Min(scale, Math.Sqrt(6_000_000 / ((double)page.DipWidth * page.DipHeight)));
                    byte[] png = await renderer.RenderAsync(index, page, new Windows.Foundation.Rect(0, 0, page.DipWidth, page.DipHeight), scale, cancel.Token);
                    if ((totalBytes += png.LongLength) > 256L * 1024 * 1024) throw new IOException("所选页面图像总量超过 256 MB，请分批导入。");
                    string output = Path.Combine(staging, index + ".png"); await File.WriteAllBytesAsync(output, png, cancel.Token);
                    prepared.Add((index, output)); progress.Value = prepared.Count; label.Text = "已准备 " + prepared.Count + " / " + indices.Count + " 页";
                }
                cancel.Token.ThrowIfCancellationRequested();
                if (!host.Board.CanAddPages(prepared.Count)) throw new IOException("当前画板无法继续增加页面。");
                // Stage all fallible I/O before changing the live board. Commit does not await input.
                var images = new List<Features.Board.BoardImageAsset>();
                foreach (var item in prepared)
                {
                    var page = file.Pages[item.Index];
                    images.Add(new Features.Board.BoardImageAsset { Name = Path.GetFileNameWithoutExtension(path) + " · 第 " + (item.Index + 1) + " 页",
                        Data = await File.ReadAllBytesAsync(item.Path, cancel.Token), Width = page.DipWidth, Height = page.DipHeight });
                }
                cancel.Token.ThrowIfCancellationRequested();
                var imported = new List<(Ink.InkController Ink, IReadOnlyList<Features.Board.BoardImageAsset> Images)>();
                try
                {
                    double dpi = (host.Owner.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
                    var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(host.Owner.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
                    float width = (float)(area.Width / dpi), height = Math.Max(100, (float)(area.Height / dpi) - 120);
                    for (int i = 0; i < prepared.Count; i++)
                    {
                        var controller = new Ink.InkController();
                        imported.Add((controller, new[] { images[i] }));
                        host.ConfigureController(controller);
                        images[i].LayerOrder = 0;
                        var ink = Core.Ink.InkStroke.CloneObjects(file.GetInk(prepared[i].Index).OrderBy(stroke => stroke.LayerOrder ?? long.MaxValue)).ToArray();
                        for (int layer = 0; layer < ink.Length; layer++) ink[layer].LayerOrder = layer + 1;
                        controller.Document.AddRange(ink);
                        float scale = Math.Min((width - 32) / images[i].Width, (height - 32) / images[i].Height);
                        controller.ViewTransform = System.Numerics.Matrix3x2.CreateScale(scale) * System.Numerics.Matrix3x2.CreateTranslation((width - images[i].Width * scale) / 2, 16);
                    }
                    if (!host.Board.InsertPagesAfterCurrent(imported)) throw new IOException("当前画板无法继续增加页面。");
                }
                finally { foreach (var page in imported) if (!host.Board.Pages.Contains(page.Ink)) page.Ink.Dispose(); }
                complete = true; progressDialog.Hide(); await shown;
                host.ShowBoard();
            }
            finally { complete = true; progressDialog.Hide(); await shown; cancel.Dispose(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await NoticeAsync("PDF 导入失败", ex.GetBaseException().Message); }
        finally
        {
            if (renderer is not null) await renderer.DisposeAsync();
            if (file is not null) await Task.Run(file.Dispose);
            CloseOperationWindow(); _operation.Dispose(); _operation = null;
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            _busy = false;
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _lifetime.Cancel(); CloseOperationWindow(); _window?.ForceClose(); _window = null;
    }
}
