using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Pdf;

/// <summary>Runs inside the real WinUI host, including native PDF rasterization and GPU ink.</summary>
public static class PdfDiagnostics
{
    public static async Task RunAsync(IDocumentWorkspaceHost host, string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); PdfWindow window = null;
        try
        {
            string source = Path.Combine(directory, "classroom.pdf"), saved = Path.Combine(directory, "annotations.pdf");
            CreateFixture(source, 6);
            var inks = CreateInk();
            using (var file = await PdfAnnotationFile.OpenAsync(source))
            {
                Check("crop rotation and user unit metadata", file.Pages.Count == 6 && file.Pages[1].NormalizedRotation == 90 && file.Pages[4].UserUnit == 2);
                Check("document bookmarks", file.Bookmarks.Count == 1 && file.Bookmarks[0].PageIndex == 5);
                var pages = Enumerable.Range(0, 5).ToDictionary(index => index, _ => (IReadOnlyList<InkStroke>)inks);
                var saveTime = Stopwatch.StartNew();
                await file.SaveAsync(saved, pages);
                long initialSize = new FileInfo(saved).Length;
                for (int repeat = 0; repeat < 3; repeat++) await file.SaveAsync(saved, pages);
                Check("repeated saves do not accumulate appearances", new FileInfo(saved).Length <= initialSize + 1024, new { bytes = initialSize, saveMilliseconds = saveTime.ElapsedMilliseconds / 4 });
            }
            using (var file = await PdfAnnotationFile.OpenAsync(saved))
            {
                Check("all brush points restore exactly", Enumerable.Range(0, 5).All(index => file.GetInk(index).Count == inks.Count && file.GetInk(index).All(stroke => stroke.Points.SequenceEqual(inks.Single(original => original.Id == stroke.Id).Points))));
                Check("original text remains searchable", (await file.SearchAsync("classroom")).Single().PageIndex == 5);
                await using var raw = await PdfRasterizer.OpenAsync(saved, null, CancellationToken.None);
                var device = CanvasDevice.GetSharedDevice();
                for (int index = 0; index < 5; index++)
                {
                    var geometry = file.Pages[index];
                    var png = await raw.RenderAsync(index, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), 1, CancellationToken.None);
                    await File.WriteAllBytesAsync(Path.Combine(directory, "pdf-" + index + ".png"), png);
                    using var native = await BitmapAsync(png, device);
                    using var controller = new InkController(); controller.Document.AddRange(inks);
                    using var reference = new CanvasRenderTarget(device, (float)native.Size.Width, (float)native.Size.Height, 96);
                    using var scene = new InkSceneRenderer();
                    using (var draw = reference.CreateDrawingSession())
                    {
                        draw.Clear(Microsoft.UI.Colors.White);
                        scene.DrawCommitted(draw, controller, Matrix3x2.CreateScale((float)native.Size.Width / geometry.DipWidth, (float)native.Size.Height / geometry.DipHeight));
                    }
                    await reference.SaveAsync(Path.Combine(directory, "ink-reference-" + index + ".png"), CanvasBitmapFileFormat.Png);
                    byte[] actual = native.GetPixelBytes(), expected = reference.GetPixelBytes();
                    long union = 0, intersection = 0, totalError = 0;
                    for (int p = 0; p < expected.Length; p += 4)
                    {
                        bool a = Math.Min(actual[p], Math.Min(actual[p + 1], actual[p + 2])) < 235;
                        bool b = Math.Min(expected[p], Math.Min(expected[p + 1], expected[p + 2])) < 235;
                        if (a || b) { union++; totalError += Math.Abs(actual[p] - expected[p]) + Math.Abs(actual[p + 1] - expected[p + 1]) + Math.Abs(actual[p + 2] - expected[p + 2]); }
                        if (a && b) intersection++;
                    }
                    double overlap = intersection / (double)Math.Max(1, union), error = totalError / (double)Math.Max(1, union * 3);
                    Check("vector appearance matches native ink page " + index, overlap > .995 && error < 1, new { overlap, meanChannelError = error, rotation = geometry.NormalizedRotation, geometry.UserUnit });
                }
            }
            await CheckMarginRenderingAsync();
            var workspaceFile = await PdfAnnotationFile.OpenAsync(source);
            var rasterizer = await PdfRasterizer.OpenAsync(workspaceFile, CancellationToken.None);
            window = new PdfWindow(host, workspaceFile, rasterizer, () => Task.CompletedTask, () => Task.CompletedTask);
            host.Attach(window); window.Activate(); await Task.Delay(900);
            Check("adjacent PDF pages are prefetched before scrolling", window.CachedPages.Contains(1)
                && window.BitmapRenderCount > window.VisiblePageCount, new { window.BitmapRenderCount, pages = window.CachedPages.ToArray() });
            Check("native document workspace exposes its active controller and window", window.Controller is not null && ReferenceEquals(window.Window, window) && window.AppWindow.IsVisible);
            Check("PDF uses shared WinUI title bar and current file title", window.ExtendsContentIntoTitleBar
                && window.Content is Controls.Controls.WindowFrame && FindVisual<TextBlock>(window.Content, "PDF 文档名称").Text == Path.GetFileName(window.FilePath));
            Check("PDF is a normal non-topmost window", window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { IsAlwaysOnTop: false }
                && !Platform.Interop.NativeMethods.HasExStyle(WinRT.Interop.WindowNative.GetWindowHandle(window), Platform.Interop.NativeMethods.WS_EX_TOPMOST));
            Check("sidebar generates realized page thumbnails", window.SidebarOpen && window.RealizedThumbnailCount > 0,
                new { thumbnails = window.RealizedThumbnailCount });
            var pageList = FindVisual<ListView>(window.Content, "PDF 页面列表");
            var navigation = FindVisual<SelectorBar>(window.Content, "PDF 导航视图");
            var pageImage = FindVisual<Microsoft.UI.Xaml.Controls.Image>((DependencyObject)pageList.ContainerFromIndex(0), "");
            var landscapeImage = FindVisual<Microsoft.UI.Xaml.Controls.Image>((DependencyObject)pageList.ContainerFromIndex(1), "");
            Check("thumbnail paper follows portrait and landscape page aspect", Math.Abs(pageImage.ActualWidth / pageImage.ActualHeight
                - workspaceFile.Pages[0].DipWidth / workspaceFile.Pages[0].DipHeight) < .001
                && Math.Abs(landscapeImage.ActualWidth / landscapeImage.ActualHeight - workspaceFile.Pages[1].DipWidth / workspaceFile.Pages[1].DipHeight) < .001,
                new { portraitWidth = pageImage.ActualWidth, portraitHeight = pageImage.ActualHeight, landscapeWidth = landscapeImage.ActualWidth, landscapeHeight = landscapeImage.ActualHeight });
            Check("thumbnail pages use the same document scale", Math.Abs(pageImage.ActualWidth / workspaceFile.Pages[0].DipWidth
                - landscapeImage.ActualWidth / workspaceFile.Pages[1].DipWidth) < .001);
            var pageFrame = (Border)pageImage.Parent;
            Check("thumbnail paper frame has no fixed aspect padding", Math.Abs(pageFrame.ActualWidth - pageImage.ActualWidth - 4) < .01
                && Math.Abs(pageFrame.ActualHeight - pageImage.ActualHeight - 4) < .01);
            var pixelsBeforeMargin = await PixelsAsync(pageImage);
            double originalThumbnailWidth = pageImage.ActualWidth, originalThumbnailHeight = pageImage.ActualHeight;
            var outsideInk = new InkStroke(new() { TaperKind = TaperKind.None, BaseWidth = 24 });
            outsideInk.AddPoint(-300, 100); outsideInk.AddPoint(-100, 250); outsideInk.Complete();
            window.Controller.Document.Add(outsideInk);
            await Task.Delay(400);
            var pixelsAfterMargin = await PixelsAsync(pageImage);
            Check("page exterior annotations do not resize or enter document thumbnail", pageImage.ActualWidth == originalThumbnailWidth
                && pageImage.ActualHeight == originalThumbnailHeight && pixelsBeforeMargin.SequenceEqual(pixelsAfterMargin));
            window.Controller.Document.Remove(outsideInk);
            pageList.ScrollIntoView(pageList.Items[4]); await Task.Delay(400);
            var largePageImage = FindVisual<Microsoft.UI.Xaml.Controls.Image>((DependencyObject)pageList.ContainerFromIndex(4), "");
            Check("thumbnail preserves relative physical page sizes and user units", Math.Abs(largePageImage.ActualWidth / originalThumbnailWidth - 2) < .01
                && Math.Abs(largePageImage.ActualHeight / originalThumbnailHeight - 2) < .01,
                new { width = largePageImage.ActualWidth, height = largePageImage.ActualHeight, originalThumbnailWidth, originalThumbnailHeight });
            pageList.ScrollIntoView(pageList.Items[0]); await Task.Delay(250);
            var searchPrevious = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "上一个搜索结果");
            var searchNext = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "下一个搜索结果");
            var floatingSearch = FindVisual<Border>(window.Content, "PDF 悬浮搜索条");
            Check("search controls remain in a closed non-modal surface", floatingSearch.Visibility == Visibility.Collapsed
                && !searchPrevious.IsEnabled && !searchNext.IsEnabled);
            navigation.SelectedItem = navigation.Items[1];
            Check("contents tab has its own navigation view", pageList.Visibility == Visibility.Collapsed);
            navigation.SelectedItem = navigation.Items[0];
            var saveState = FindVisual<TextBlock>(window.Content, "PDF 保存状态");
            var toolbar = FindVisual<Grid>(window.Content, "PDF 阅读工具栏");
            var readerRoot = (Grid)toolbar.Parent;
            var split = FindVisual<SplitView>(window.Content, "");
            var chrome = (Controls.Controls.WindowFrame)window.Content;
            Check("document reaches the bottom without a fixed status bar", readerRoot.RowDefinitions.Count == 2
                && Math.Abs(split.ActualHeight + toolbar.ActualHeight - readerRoot.ActualHeight) <= 1);
            Check("save state belongs to native title bar", chrome.Header.Content is Grid titleContent
                && ReferenceEquals((saveState.Parent as FrameworkElement)?.Parent, titleContent.Children.OfType<Border>().Single()) && chrome.Header.ActualHeight <= 40);
            window.Controller.Tool = InkTool.Pan;
            var readMode = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "阅读模式");
            Check("read toggle tracks the active PDF tool", readMode.IsSelected);
            window.Controller.Tool = InkTool.Pen;
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-thumbnails.png"));
            float expandedWidth = window.ViewportSize.X;
            window.SetSidebarOpen(false); await Task.Delay(150);
            Check("collapsing sidebar gives its width back to the page", !window.SidebarOpen && window.ViewportSize.X > expandedWidth + 150);
            window.SetSidebarOpen(true); await Task.Delay(150);
            Check("sidebar expands without shifting the page outside viewport", window.SidebarOpen && Math.Abs(window.ViewportSize.X - expandedWidth) < 2);
            var windowSize = window.AppWindow.ClientSize;
            double windowDpi = ((FrameworkElement)window.Content).XamlRoot.RasterizationScale;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)Math.Ceiling(620 * windowDpi),
                (int)Math.Ceiling(720 * windowDpi))); await Task.Delay(150);
            Check("narrow window folds sidebar to preserve page width", !window.SidebarOpen && window.ViewportSize.X > 550);
            Check("narrow toolbar stays on one compact row", toolbar.ActualHeight == 44);
            window.SetSidebarOpen(true); await Task.Delay(150);
            Check("narrow overlay sidebar has an opaque readable surface", ((SolidColorBrush)split.PaneBackground).Color.A == 255);
            Check("narrow window uses overlay sidebar", window.SidebarOpen && window.ViewportSize.X > 550,
                new { window.SidebarOpen, window.SidebarMode, width = window.ViewportSize.X });
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-narrow-sidebar.png"));
            window.SetSidebarOpen(false); await Task.Delay(80);
            Check("sidebar remains folded on narrow window resize", !window.SidebarOpen);
            window.AppWindow.ResizeClient(windowSize); await Task.Delay(150); window.SetSidebarOpen(true); await Task.Delay(150);

            var toggle = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "显示或隐藏 PDF 侧栏");
            ((IInvokeProvider)new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(180);
            Check("single persistent navigation toggle folds the sidebar", !window.SidebarOpen);
            ((IInvokeProvider)new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(180);
            Check("persistent navigation toggle reopens sidebar", window.SidebarOpen && toggle.IsSelected);
            foreach (int width in new[] { 560, 620, 760, 920, 1120, 1440 })
            {
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)Math.Ceiling(width * windowDpi), (int)Math.Ceiling(760 * windowDpi)));
                await Task.Delay(100);
                var groups = toolbar.Children.OfType<StackPanel>().ToArray();
                var bounds = groups.Select(group => group.TransformToVisual(toolbar).TransformBounds(new(0, 0, group.ActualWidth, group.ActualHeight))).ToArray();
                Check("responsive three-part toolbar has no overlaps " + width,
                    toolbar.ActualHeight == 44 && bounds.All(rect => rect.Left >= -1 && rect.Right <= toolbar.ActualWidth + 1 && rect.Height <= 40)
                    && bounds[0].Right <= bounds[1].Left + 1 && bounds[1].Right <= bounds[2].Left + 1, bounds);
            }
            window.AppWindow.ResizeClient(windowSize); await Task.Delay(150); window.SetSidebarOpen(true); await Task.Delay(150);
            var saveButton = FindVisual<SplitButton>(window.Content, "保存 PDF");
            Check("save split button uses the native save symbol", saveButton.Content is SymbolIcon { Symbol: Symbol.Save }
                && saveButton.Flyout is MenuFlyout saveMenu && saveMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text.StartsWith("另存为")));
            Check("reader icon commands use native Fluent codepoints", toggle.IconFont == "Segoe Fluent Icons"
                && FindVisual<Controls.Controls.TouchIconButton>(window.Content, "搜索").Glyph == ((char)Symbol.Find).ToString());
            var pageNumber = FindVisual<TextBox>(window.Content, "PDF 页码");
            Check("page input is compact without spin buttons", pageNumber.ActualWidth <= 50);
            window.Activate();
            bool pageFocused = pageNumber.Focus(FocusState.Programmatic);
            await Task.Delay(60);
            pageNumber.Text = "3";
            bool saveFocused = FindVisual<SplitButton>(window.Content, "保存 PDF").Focus(FocusState.Programmatic);
            await Task.Delay(100);
            Check("page input commits through focus loss", window.PageIndex == 2 && pageNumber.Text == "3",
                new { pageFocused, saveFocused, window.PageIndex, pageNumber.Text });
            window.GoToPage(0);
            var readingButton = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "阅读模式");
            window.Controller.Tool = InkTool.Highlighter;
            ((IInvokeProvider)new ButtonAutomationPeer(readingButton).GetPattern(PatternInterface.Invoke)).Invoke();
            bool enteredReadMode = window.Controller.Tool == InkTool.Pan && readingButton.IsSelected;
            ((IInvokeProvider)new ButtonAutomationPeer(readingButton).GetPattern(PatternInterface.Invoke)).Invoke();
            Check("reading toggle restores the last annotation tool", enteredReadMode && window.Controller.Tool == InkTool.Highlighter && !readingButton.IsSelected);
            window.Controller.Tool = InkTool.Pen;
            var fitButton = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "切换整页或页宽");
            ((IInvokeProvider)new ButtonAutomationPeer(fitButton).GetPattern(PatternInterface.Invoke)).Invoke();
            var widthFitScale = window.Controller.ViewTransform.M11;
            ((IInvokeProvider)new ButtonAutomationPeer(fitButton).GetPattern(PatternInterface.Invoke)).Invoke();
            Check("fit toggle switches width and full page", widthFitScale > window.Controller.ViewTransform.M11);

            var vertical = FindVisual<ScrollBar>(window.Content, "PDF 垂直滚动条");
            var horizontal = FindVisual<ScrollBar>(window.Content, "PDF 水平滚动条");
            Check("document has visible native scroll bars on both axes", vertical.ActualWidth >= 20 && horizontal.ActualHeight >= 20
                && vertical.Maximum > 0 && horizontal.IsEnabled == (horizontal.Maximum > .01),
                new { verticalWidth = vertical.ActualWidth, horizontalHeight = horizontal.ActualHeight, verticalRange = vertical.Maximum, horizontalRange = horizontal.Maximum });
            var body = ((Controls.Controls.WindowFrame)window.Content).Children.OfType<Grid>().Single();
            var viewport = (Grid)window.AnnotationSurface.Parent;
            var originalTheme = body.RequestedTheme;
            body.RequestedTheme = ElementTheme.Dark; await Task.Delay(180);
            var darkBackground = (SolidColorBrush)viewport.Background;
            Check("dark PDF workspace exposes its Mica foundation through a content layer", window.SystemBackdrop is MicaBackdrop
                && darkBackground.Color == Windows.UI.Color.FromArgb(76, 58, 58, 58) && window.AnnotationSurface.ActualTheme == ElementTheme.Dark);
            Check("dark PDF commands retain readable foreground", ((SolidColorBrush)toggle.ContentForeground).Color == Microsoft.UI.Colors.White);
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-dark-navigation.png"));
            var backdrop = window.SystemBackdrop;
            window.SystemBackdrop = null;
            body.RequestedTheme = ElementTheme.Light; await Task.Delay(180);
            var lightBackground = (SolidColorBrush)viewport.Background;
            Check("light PDF workspace has a solid themed fallback when the backdrop is unavailable", lightBackground.Color == Windows.UI.Color.FromArgb(255, 243, 243, 243));
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-light-navigation.png"));
            window.SystemBackdrop = backdrop;
            body.RequestedTheme = originalTheme;
            window.ApplySettings(); await Task.Delay(150);

            var navigationLayout = new PdfContinuousLayout(workspaceFile.Pages);
            Vector2 DocumentPosition() => navigationLayout.DocumentView(window.PageIndex, window.Controller.ViewTransform).Translation;
            window.GoToPage(0);
            var scrollStart = DocumentPosition();
            window.SmoothScrollBy(new(0, -240));
            Check("wheel scrolling queues a transition without an immediate jump", DocumentPosition() == scrollStart);
            await Task.Delay(80);
            float intermediateScroll = scrollStart.Y - DocumentPosition().Y;
            Check("wheel scrolling renders intermediate positions", intermediateScroll > 1 && intermediateScroll < 240);
            Check("scroll uses cached composition paper and ink", window.UsesNavigationPicture && window.UsesCompositionScroll
                && window.NavigationPictureBytes <= 24_100_000, new { window.NavigationPictureBytes, window.NavigationPictureDraws });
            await Task.Delay(650);
            Check("wheel scrolling settles at target and synchronizes thumb", Math.Abs(scrollStart.Y - DocumentPosition().Y - 240) < 1
                && Math.Abs(vertical.Value - (16 - DocumentPosition().Y)) < 1);
            vertical.Value = vertical.Maximum; await Task.Delay(150);
            Check("vertical scrollbar reaches the final page", window.PageIndex == workspaceFile.Pages.Count - 1
                && Math.Abs(vertical.Value - vertical.Maximum) < 1);
            window.GoToPage(0);
            window.Controller.ViewTransform *= Matrix3x2.CreateScale(3, window.ViewportSize / 2);
            window.Controller.NotifyElementsChanged(); await Task.Delay(100);
            horizontal.Value = horizontal.Maximum; await Task.Delay(100);
            Check("horizontal scrollbar pans a zoomed document without leaving page bounds", horizontal.Maximum > 0
                && Math.Abs(DocumentPosition().X - (window.ViewportSize.X - navigationLayout.Width * MathF.Sqrt(window.Controller.ViewTransform.GetDeterminant()) - 16)) < 1);

            await TouchNavigationAsync(InkTool.Pen, pinch: false);
            Check("two-finger page drag uses composition cache", window.UsesNavigationPicture);
            var release = DocumentPosition(); await Task.Delay(180);
            Check("single-finger ink mode permits two-finger inertial page scrolling without stray ink", DocumentPosition().Y < release.Y - 8
                && window.Controller.Document.Count == 0 && window.Controller.LiveStrokes.Count() == 0);
            var interrupted = new OverlayPointer(952, 160, 180, .5f, 4, 4, OverlayPointerKind.Touch, 3000);
            window.NavigationInputStarting(interrupted);
            var interruptedAt = DocumentPosition(); await Task.Delay(150);
            Check("new contact stops the previous scroll immediately", Vector2.Distance(DocumentPosition(), interruptedAt) < .01f);
            Check("interrupting inertia restores editable ink surface", !window.UsesNavigationPicture && !window.UsesCompositionScroll);
            await TouchNavigationAsync(InkTool.Pen, pinch: true);
            var pinchRelease = DocumentPosition(); await Task.Delay(180);
            Check("pinch zoom stops without accidental translation momentum", Vector2.Distance(DocumentPosition(), pinchRelease) < .01f);
            await TouchNavigationAsync(InkTool.Pan, pinch: false);
            var handRelease = DocumentPosition(); await Task.Delay(180);
            Check("hand mode retains direct drag with no release inertia", Vector2.Distance(DocumentPosition(), handRelease) < .01f);
            window.GoToPage(0); await Task.Delay(300);
            var burstController = window.Controller;
            var burstPointer = new OverlayPointer(960, 160, 300, .5f, 4, 4, OverlayPointerKind.Touch, 4000);
            window.NavigationInputStarting(burstPointer); burstController.OnPointerDown(burstPointer);
            int viewportBefore = window.ViewportUpdateCount, picturesBefore = window.NavigationPictureDraws;
            var burstTimer = Stopwatch.StartNew();
            for (int sample = 1; sample <= 200; sample++)
            {
                burstPointer = burstPointer with { Y = 300 - sample * .5f, TimeMs = (uint)(4000 + sample) };
                burstController.OnPointerMove(burstPointer);
            }
            long inputMilliseconds = burstTimer.ElapsedMilliseconds;
            Check("coalesced digitizer packets do not update XAML per packet", window.ViewportUpdateCount == viewportBefore);
            await Task.Delay(50);
            Check("burst touch samples share a frame and cached picture", window.UsesNavigationPicture
                && window.ViewportUpdateCount - viewportBefore < 10 && window.NavigationPictureDraws - picturesBefore < 10,
                new { samples = 200, inputMilliseconds, frames = window.ViewportUpdateCount - viewportBefore,
                    pictures = window.NavigationPictureDraws - picturesBefore });
            burstController.OnPointerUp(burstPointer); window.NavigationInputEnded(); await Task.Delay(80);
            Check("hand release restores the live ink surface after cached panning", !window.UsesNavigationPicture && !window.UsesCompositionScroll
                && !window.NavigationHandoverPending);
            window.GoToPage(0); window.Controller.Tool = InkTool.Pen;

            var continuous = new PdfContinuousLayout(workspaceFile.Pages);
            window.GoToPage(0);
            var documentView = continuous.DocumentView(window.PageIndex, window.Controller.ViewTransform);
            float boundary = Vector2.Transform(new(0, continuous[0].Bottom), documentView).Y;
            window.ScrollBy(window.ViewportSize.Y / 2 - boundary);
            await Task.Delay(250);
            Check("continuous viewport shows both pages at a boundary", window.VisiblePageCount >= 2);
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-continuous-boundary.png"));
            documentView = continuous.DocumentView(window.PageIndex, window.Controller.ViewTransform);
            var secondPagePoint = Vector2.Transform(new(workspaceFile.Pages[1].DipWidth / 2, 30), continuous.PageView(1, documentView));
            var secondInput = new OverlayPointer(432, secondPagePoint.X, secondPagePoint.Y, .5f, 0, 0, OverlayPointerKind.Pen, 500);
            window.ActivateTouchedPage(secondInput);
            Check("touching adjacent page rebinds ink without changing document view", window.PageIndex == 1
                && MatrixDistance(documentView, continuous.DocumentView(window.PageIndex, window.Controller.ViewTransform)) < .001f);
            window.Controller.RecognizeShapes = false;
            window.Controller.OnPointerDown(secondInput);
            window.Controller.OnPointerMove(secondInput with { X = secondInput.X + 30, Y = secondInput.Y + 6, TimeMs = 520 });
            window.Controller.OnPointerUp(secondInput with { X = secondInput.X + 50, Y = secondInput.Y + 12, TimeMs = 540 });
            window.Controller.FinishDotAnimations();
            Check("adjacent page annotation has independent history", window.Controller.Document.Count == 1 && window.Controller.History.CanUndo);
            window.Controller.Undo();
            Check("adjacent page undo retains other pages", window.Controller.Document.Count == 0);
            // Exercise the XAML pointer entry point, including InputStarting and page rebinding.
            window.GoToPage(0); await Task.Delay(150);
            window.Controller.Tool = InkTool.Pen;
            window.Controller.RecognizeShapes = false;
            var surface = window.AnnotationSurface;
            var pageRight = Vector2.Transform(new(workspaceFile.Pages[0].DipWidth, 180), window.Controller.ViewTransform);
            var local = new Vector2(Math.Min(window.ViewportSize.X - 12, pageRight.X + 18), pageRight.Y);
            var screen = surface.TransformToVisual(null).TransformPoint(new(local.X, local.Y));
            var origin = new Platform.Interop.NativeMethods.POINT();
            ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin);
            float dpi = (float)surface.XamlRoot.RasterizationScale;
            bool nativeInput = GetCursorPos(out var oldCursor);
            bool inputBegan = false;
            surface.InputBegan += () => inputBegan = true;
            try
            {
                SetCursorPos(origin.X + (int)(screen.X * dpi), origin.Y + (int)(screen.Y * dpi));
                var hit = WindowFromPoint(new Platform.Interop.NativeMethods.POINT { X = origin.X + (int)(screen.X * dpi), Y = origin.Y + (int)(screen.Y * dpi) });
                nativeInput &= GetAncestor(hit, 2) == WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (nativeInput)
                {
                    MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(40);
                    SetCursorPos(origin.X + (int)(screen.X * dpi), origin.Y + (int)((screen.Y + 30) * dpi)); await Task.Delay(40);
                    MouseEvent(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(180);
                    window.Controller.FinishDotAnimations();
                }
                if (nativeInput)
                {
                    Check("native pointer can annotate page margin", inputBegan && window.Controller.Document.Count == 1
                        && window.Controller.Document.Strokes[0].Bounds.Right > workspaceFile.Pages[0].DipWidth,
                        new { hit = hit.ToInt64(), window = WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64(), count = window.Controller.Document.Count,
                            bounds = window.Controller.Document.Bounds, screen, dpi });
                    window.Controller.Undo();
                }
                else
                {
                    checks.Add(new { check = "native pointer margin input unavailable", available = false, detail = "The PDF window is not reachable on the current input desktop. Margin ink is verified through programmatic pointer samples." });
                    var sample = new OverlayPointer(441, local.X, local.Y, .5f, 0, 0, OverlayPointerKind.Pen, 1000);
                    window.ActivateTouchedPage(sample);
                    window.Controller.OnPointerDown(sample);
                    window.Controller.OnPointerMove(sample with { Y = sample.Y + 30, TimeMs = 1030 });
                    window.Controller.OnPointerUp(sample with { Y = sample.Y + 35, TimeMs = 1040 });
                    window.Controller.FinishDotAnimations();
                    Check("page margin input commits to the nearest page without clipping", surface.ContentBounds is null && window.Controller.Document.Count == 1
                        && window.Controller.Document.Strokes[0].Bounds.Right > workspaceFile.Pages[0].DipWidth);
                    window.Controller.Undo();
                }
            }
            finally { if (nativeInput) MouseEvent(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(oldCursor.X, oldCursor.Y); }
            window.GoToPage(0); await Task.Delay(100);
            var pen = window.Controller; pen.RecognizeShapes = false;
            var first = Vector2.Transform(new(140, 200), pen.ViewTransform);
            var input = new OverlayPointer(431, first.X, first.Y, .5f, 4, 4, OverlayPointerKind.Touch, 1000);
            pen.OnPointerDown(input);
            for (int i = 1; i <= 45; i++)
            {
                var point = Vector2.Transform(new(140 + i * 4, 200 + MathF.Sin(i / 5f) * 24), pen.ViewTransform);
                input = input with { X = point.X, Y = point.Y, TimeMs = (uint)(1000 + i * 6) }; pen.OnPointerMove(input);
            }
            pen.OnPointerUp(input); pen.FinishDotAnimations();
            Check("original touch ink controller commits PDF annotation", pen.Document.Count == 1 && window.IsDirty);
            var expectedPoints = pen.Document.Strokes[0].Points.ToArray();
            window.GoToPage(1); var second = window.Controller;
            window.GoToPage(0);
            Check("page changes restore independent ink and history", !ReferenceEquals(pen, second) && ReferenceEquals(pen, window.Controller) && pen.Document.Count == 1 && second.Document.Count == 0);
            Check("native window save", await window.SaveAsync() && !window.IsDirty);
            Check("title bar reports successful save", saveState.Text == "已保存");
            var concurrentInk = new InkStroke(new() { TaperKind = TaperKind.None });
            concurrentInk.AddPoint(300, 300); concurrentInk.AddPoint(330, 330); concurrentInk.Complete();
            var concurrentSave = window.SaveAsync();
            window.Controller.Document.Add(concurrentInk);
            await concurrentSave;
            Check("editing during a save leaves a truthful unsaved status", window.IsDirty && saveState.Text == "未保存"
                && !window.StatusText.StartsWith("已保存 ·", StringComparison.Ordinal));
            window.Controller.Document.Remove(concurrentInk); await window.SaveAsync();
            using (var restored = await PdfAnnotationFile.OpenAsync(source)) Check("workspace edits persist inside PDF", restored.GetInk(0).Single().Points.SequenceEqual(expectedPoints));
            window.SetSidebarOpen(false); await Task.Delay(100);
            var searchCommand = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "搜索");
            ((IInvokeProvider)new ButtonAutomationPeer(searchCommand).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
            float searchViewportWidth = window.ViewportSize.X;
            Check("search opens a floating bar with sidebar unchanged", floatingSearch.Visibility == Visibility.Visible && !window.SidebarOpen);
            await window.SearchAsync("classroom");
            Check("search navigates to highlighted result without expanding sidebar", !window.SidebarOpen && window.SearchResultCount == 1 && window.PageIndex == 5
                && window.ViewportSize.X == searchViewportWidth);
            Check("floating search enables result navigation", searchPrevious.IsEnabled && searchNext.IsEnabled);
            window.SetSidebarOpen(true); navigation.SelectedItem = navigation.Items[2];
            Check("sidebar can show the same search results", FindVisual<ListView>(window.Content, "PDF 搜索结果").Visibility == Visibility.Visible);
            window.NextSearchResult(1); window.NextSearchResult(-1);
            Check("search previous next wraps around results", window.PageIndex == 5);
            await Task.Delay(250); await ScreenshotAsync(window, Path.Combine(directory, "pdf-search.png"));
            await window.SearchAsync("missing classroom phrase");
            Check("search clears stale highlights when no match", window.SearchResultCount == 0);
            Check("empty search result disables navigation buttons", !searchPrevious.IsEnabled && !searchNext.IsEnabled);
            await window.SearchAsync("");
            Check("clearing search resets transient status and keeps input available", window.SearchResultCount == 0 && !window.StatusText.StartsWith("找到", StringComparison.Ordinal)
                && ReferenceEquals(navigation.SelectedItem, navigation.Items[2]));
            var closeSearch = FindVisual<Controls.Controls.TouchIconButton>(window.Content, "关闭 PDF 搜索");
            ((IInvokeProvider)new ButtonAutomationPeer(closeSearch).GetPattern(PatternInterface.Invoke)).Invoke();
            Check("closing floating search clears highlights and preserves document input", floatingSearch.Visibility == Visibility.Collapsed && window.SearchResultCount == 0);
            navigation.SelectedItem = navigation.Items[0];
            for (int i = 0; i < 16; i++) { window.GoToPage(i % 6); await Task.Delay(40); }
            window.GoToPage(0); await Task.Delay(800);
            Check("preview cache remains bounded", window.PreviewCacheBytes <= 16 * 1024 * 1024 && window.VisiblePreviewBytes <= 104 * 1024 * 1024,
                new { bytes = window.PreviewCacheBytes, visibleBytes = window.VisiblePreviewBytes, workingSet = Process.GetCurrentProcess().WorkingSet64 });
            await ScreenshotAsync(window, Path.Combine(directory, "pdf-window.png"));
            long warmPreviewBytes = window.VisiblePreviewBytes;
            var warmController = window.Controller;
            var warmView = warmController.ViewTransform;
            int warmUndo = warmController.History.UndoCount;
            window.AppWindow.Hide();
            Check("hidden PDF releases page rendering resources", window.ReleaseInactiveResources() && window.VisiblePreviewBytes == 0,
                new { warmPreviewBytes, releasedBytes = warmPreviewBytes });
            Check("hidden PDF releases composition cache", window.NavigationPictureBytes == 0);
            await Task.Delay(200);
            Check("hidden PDF does not recreate page previews", window.VisiblePreviewBytes == 0);
            var restoreTimer = Stopwatch.StartNew();
            window.Activate();
            for (int i = 0; i < 40 && window.VisiblePreviewBytes == 0; i++) await Task.Delay(50);
            Check("restoring PDF preserves page ink undo and view", window.VisiblePreviewBytes > 0
                && ReferenceEquals(window.Controller, warmController) && window.Controller.ViewTransform == warmView
                && window.Controller.History.UndoCount == warmUndo,
                new { milliseconds = restoreTimer.ElapsedMilliseconds, bytes = window.VisiblePreviewBytes });
            Check("window closes cleanly", await window.CloseAsync()); window = null;
            CreateFixture(Path.Combine(directory, "many-pages.pdf"), 160);
            using (var large = await PdfAnnotationFile.OpenAsync(Path.Combine(directory, "many-pages.pdf")))
            {
                var watch = Stopwatch.StartNew(); await using var renderer = await PdfRasterizer.OpenAsync(large, CancellationToken.None);
                for (int index = 0; index < 30; index++) await renderer.PreviewAsync(index, large.Pages[index], CancellationToken.None);
                Check("large PDFs render on demand with bounded cache", renderer.CachedBytes <= 16 * 1024 * 1024, new { pages = large.Pages.Count, renderedPages = 30, milliseconds = watch.ElapsedMilliseconds, renderer.CachedBytes });
            }
            var longFile = await PdfAnnotationFile.OpenAsync(Path.Combine(directory, "many-pages.pdf"));
            var longRasterizer = await PdfRasterizer.OpenAsync(longFile, CancellationToken.None);
            window = new PdfWindow(host, longFile, longRasterizer, () => Task.CompletedTask, () => Task.CompletedTask);
            host.Attach(window); window.Activate(); await Task.Delay(100);
            window.Controller.ViewTransform = Matrix3x2.CreateScale(.02f) * Matrix3x2.CreateTranslation(16, 16);
            window.Controller.NotifyElementsChanged(); await Task.Delay(100);
            for (int sample = 0; sample < 80 && window.PreviewWorkerRunning; sample++) await Task.Delay(50);
            int loaded = window.BitmapRenderCount;
            Check("zoomed-out long PDF finishes bounded prefetch without eviction loop", !window.PreviewWorkerRunning
                && window.VisiblePreviewBytes <= 104 * 1024 * 1024 && window.VisiblePageCount > 6,
                new { window.VisiblePageCount, window.VisiblePreviewBytes, loaded });
            await Task.Delay(150);
            Check("settled PDF preview worker stops without further rasterization", !window.PreviewWorkerRunning && window.BitmapRenderCount == loaded);
            await window.CloseAsync(); window = null;
            string fixtures = Path.GetFullPath(Path.Combine(directory, "..", "pdf-fixtures"));
            if (Directory.Exists(fixtures))
            foreach (string path in Directory.GetFiles(fixtures, "*.pdf"))
            {
                using var original = await PdfAnnotationFile.OpenAsync(path);
                await using var before = await PdfRasterizer.OpenAsync(original, CancellationToken.None);
                var preview = await before.PreviewAsync(0, original.Pages[0], CancellationToken.None);
                string output = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "-annotated.pdf");
                await original.SaveAsync(output, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = inks });
                using var reloaded = await PdfAnnotationFile.OpenAsync(output);
                await using var after = await PdfRasterizer.OpenAsync(reloaded, CancellationToken.None);
                var restored = await after.PreviewAsync(0, reloaded.Pages[0], CancellationToken.None);
                using var sourceBitmap = await BitmapAsync(preview, CanvasDevice.GetSharedDevice());
                using var restoredBitmap = await BitmapAsync(restored, CanvasDevice.GetSharedDevice());
                Check("external PDF original content preserved " + Path.GetFileName(path), reloaded.Pages.Count == original.Pages.Count && sourceBitmap.GetPixelBytes().SequenceEqual(restoredBitmap.GetPixelBytes()),
                    new { pages = original.Pages.Count, sourceBytes = new FileInfo(path).Length, savedBytes = new FileInfo(output).Length });
            }
        }
        catch (Exception ex) { checks.Add(new { check = "unhandled PDF regression error", pass = false, detail = ex.ToString() }); }
        finally
        {
            if (window is not null) { window.ForceClose(); await window.Cleanup; }
            await File.WriteAllTextAsync(Path.Combine(directory, "pdf.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });

        async Task TouchNavigationAsync(InkTool tool, bool pinch)
        {
            window.GoToPage(0);
            var controller = window.Controller;
            controller.Tool = tool;
            controller.MultiTouchEnabled = false;
            controller.TouchGesturesEnabled = true;
            controller.ConfigureGestures(true, true, false);
            var first = new OverlayPointer(950, 160, 300, .5f, 4, 4, OverlayPointerKind.Touch, 2000);
            var second = first with { Id = 951, X = 310 };
            window.NavigationInputStarting(first); controller.OnPointerDown(first);
            window.NavigationInputStarting(second); controller.OnPointerDown(second);
            for (int sample = 1; sample <= 10; sample++)
            {
                await Task.Delay(12);
                first = first with { Y = pinch ? 300 : 300 - sample * 9, TimeMs = (uint)(2000 + sample * 12) };
                second = second with { X = pinch ? 310 + sample * 15 : 310, Y = first.Y, TimeMs = first.TimeMs };
                controller.OnPointerMove(first); controller.OnPointerMove(second);
            }
            controller.OnPointerUp(second); controller.OnPointerUp(first);
            window.NavigationInputEnded();
        }

        async Task CheckMarginRenderingAsync()
        {
            string marginSource = Path.Combine(directory, "margin-source.pdf"), marginSaved = Path.Combine(directory, "margin-saved.pdf");
            CreateFixture(marginSource, 5);
            using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(marginSource, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
            {
                foreach (PdfPage page in pdf.Pages)
                    page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("q 0 1 0 rg 0 0 600 800 re f 1 1 1 rg 20 30 560 740 re f 0 0 0 rg 80 100 40 40 re f Q"));
                pdf.Save(marginSource);
            }
            using var file = await PdfAnnotationFile.OpenAsync(marginSource);
            var pages = new Dictionary<int, IReadOnlyList<InkStroke>>();
            for (int index = 0; index < file.Pages.Count; index++)
            {
                var page = file.Pages[index];
                var strokes = new List<InkStroke>();
                foreach (var point in new[] { new Vector2(-40, 200), new(200, -40), new(page.DipWidth + 40, 200), new(200, page.DipHeight + 40) })
                {
                    var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 20, TaperKind = TaperKind.None, Color = InkColor.Red, Nib = PenNib.Ellipse45 });
                    stroke.AddPoint(point.X, point.Y); stroke.AddPoint(point.X + 12, point.Y + 15); stroke.Complete(); strokes.Add(stroke);
                }
                pages[index] = strokes;
            }
            await file.SaveAsync(marginSaved, pages);
            using var restored = await PdfAnnotationFile.OpenAsync(marginSaved);
            await using var original = await PdfRasterizer.OpenAsync(file, CancellationToken.None);
            await using var output = await PdfRasterizer.OpenAsync(marginSaved, null, CancellationToken.None);
            var device = CanvasDevice.GetSharedDevice();
            for (int index = 0; index < file.Pages.Count; index++)
            {
                var page = file.Pages[index]; var expanded = restored.Pages[index];
                var offset = PdfPageBleed.InkOffset(page, expanded);
                using var background = await BitmapAsync(await original.RenderAsync(index, page, new(0, 0, page.DipWidth, page.DipHeight), 1, CancellationToken.None), device);
                using var actual = await BitmapAsync(await output.RenderAsync(index, expanded, new(0, 0, expanded.DipWidth, expanded.DipHeight), 1, CancellationToken.None), device);
                using var expected = new CanvasRenderTarget(device, (float)actual.Size.Width, (float)actual.Size.Height, 96);
                using var controller = new InkController(); controller.Document.AddRange(pages[index]);
                using var scene = new InkSceneRenderer();
                var scale = new Vector2((float)actual.Size.Width / expanded.DipWidth, (float)actual.Size.Height / expanded.DipHeight);
                using (var draw = expected.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.White);
                    draw.DrawImage(background, new Windows.Foundation.Rect(offset.X * scale.X, offset.Y * scale.Y, page.DipWidth * scale.X, page.DipHeight * scale.Y), background.Bounds);
                    scene.DrawCommitted(draw, controller, Matrix3x2.CreateTranslation(offset) * Matrix3x2.CreateScale(scale));
                }
                byte[] a = actual.GetPixelBytes(), b = expected.GetPixelBytes();
                long redUnion = 0, redIntersection = 0, exposedGreen = 0;
                var actualContent = Core.Ink.Rect.Empty;
                var expectedContent = Core.Ink.Rect.Empty;
                int width = (int)actual.SizeInPixels.Width;
                for (int pixel = 0; pixel < a.Length; pixel += 4)
                {
                    bool redA = a[pixel] < 100 && a[pixel + 1] < 100 && a[pixel + 2] > 150;
                    bool redB = b[pixel] < 100 && b[pixel + 1] < 100 && b[pixel + 2] > 150;
                    if (redA || redB) redUnion++;
                    if (redA && redB) redIntersection++;
                    if (a[pixel] < 100 && a[pixel + 1] > 180 && a[pixel + 2] < 100) exposedGreen++;
                    int x = pixel / 4 % width, y = pixel / 4 / width;
                    if (Math.Max(a[pixel], Math.Max(a[pixel + 1], a[pixel + 2])) < 100)
                        actualContent = actualContent.Union(new(x, y, x + 1, y + 1));
                    if (Math.Max(b[pixel], Math.Max(b[pixel + 1], b[pixel + 2])) < 100)
                        expectedContent = expectedContent.Union(new(x, y, x + 1, y + 1));
                }
                double overlap = redIntersection / (double)Math.Max(1, redUnion);
                float contentShift = new[] { Math.Abs(actualContent.Left - expectedContent.Left), Math.Abs(actualContent.Top - expectedContent.Top),
                    Math.Abs(actualContent.Right - expectedContent.Right), Math.Abs(actualContent.Bottom - expectedContent.Bottom) }.Max();
                // Re-rasterizing vector content at a different pixel origin can move an edge by one pixel.
                Check("margin ink and original crop render in place page " + index,
                    overlap > .995 && exposedGreen == 0 && !actualContent.IsEmpty && contentShift <= 2,
                    new { overlap, exposedGreen, contentShift, page.Rotation, page.UserUnit });
                await actual.SaveAsync(Path.Combine(directory, "margin-" + index + ".png"), CanvasBitmapFileFormat.Png);
                await expected.SaveAsync(Path.Combine(directory, "margin-reference-" + index + ".png"), CanvasBitmapFileFormat.Png);
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Platform.Interop.NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Platform.Interop.NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Platform.Interop.NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);

    private static float MatrixDistance(Matrix3x2 a, Matrix3x2 b)
        => Vector2.Distance(a.Translation, b.Translation) + Math.Abs(a.M11 - b.M11) + Math.Abs(a.M22 - b.M22);
    private static List<InkStroke> CreateInk()
    {
        var result = new List<InkStroke>();
        foreach (var nib in Enum.GetValues<PenNib>())
        foreach (var taper in new[] { TaperKind.None, TaperKind.Tail, TaperKind.Calligraphy })
        {
            float y = 150 + result.Count * 64;
            var stroke = new InkStroke(new StrokeAttributes { Color = InkColor.Red, BaseWidth = 15, Nib = nib, TaperKind = taper });
            for (int p = 0; p < 140; p++) stroke.AddPoint(90 + p * 3, y + MathF.Sin(p / 12f) * 16, .5f, 1000 + p * 5);
            stroke.Complete(); result.Add(stroke);
        }
        var marker = new InkStroke(new StrokeAttributes { Color = new InkColor(255, 255, 210, 0), BaseWidth = 36, IsHighlighter = true, HighlighterAlpha = 96, Nib = PenNib.Ellipse45 }) { LayerOrder = 2 };
        for (int p = 0; p < 180; p++) marker.AddPoint(100 + MathF.Sin(p / 30f) * 95 + p * 1.8f, 600 + MathF.Cos(p / 18f) * 22, .5f, 1000 + p * 4);
        marker.Complete(); result.Add(marker);
        var dot = new InkStroke(new StrokeAttributes { Color = InkColor.Black, BaseWidth = 20, TaperKind = TaperKind.None }); dot.AddPoint(540, 300); dot.Complete(); result.Add(dot);
        var under = new InkStroke(new StrokeAttributes { Color = InkColor.Black, BaseWidth = 14, TaperKind = TaperKind.None }) { LayerOrder = 1 };
        under.AddPoint(90, 600); under.AddPoint(550, 600); under.Complete(); result.Add(under);
        var longStroke = new InkStroke(new StrokeAttributes { Color = InkColor.Red, BaseWidth = 7, TaperKind = TaperKind.Velocity });
        for (int p = 0; p < 1100; p++) longStroke.AddPoint(100 + p * .4f, 700 + MathF.Sin(p / 22f) * 12, .5f, p * 4);
        longStroke.Complete(); result.Add(longStroke);
        foreach (bool pressure in new[] { false, true })
        {
            var wash = new InkStroke(new StrokeAttributes { BaseWidth = 30, TaperKind = TaperKind.InkWash, InkWashSeed = 137,
                InkWash = pressure ? new(.85f, 62, .7f) : InkWashBrushOptions.Default });
            for (int p = 0; p < 180; p++)
                wash.AddPoint(80 + p * 2.8f, (pressure ? 830 : 62) + MathF.Sin(p * .06f) * 16,
                    .5f + .3f * MathF.Sin(p * .025f), p * 4, pressure, pressure ? 25 : 0, pressure ? -35 : 0);
            wash.Complete(); result.Add(wash);
        }
        return result;
    }
    private static void CreateFixture(string path, int count)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (int p = 0; p < count; p++)
        {
            var page = document.AddPage(); page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800); page.Rotate = (p % 4) * 90;
            page.CropBox = new PdfRectangle(new XRect(20, 30, 560, 740));
            if (p == 4) page.Elements.SetReal("/UserUnit", 2);
            if (p == 5)
            {
                var font = new PdfDictionary(document); font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica"); document.Internals.AddObject(font);
                var fonts = new PdfDictionary(document); fonts.Elements["/F1"] = font.Reference;
                var resources = new PdfDictionary(document); resources.Elements["/Font"] = fonts; page.Elements["/Resources"] = resources;
                var content = new PdfDictionary(document); content.CreateStream(Encoding.ASCII.GetBytes("BT /F1 20 Tf 70 720 Td (InkCanvas classroom PDF) Tj ET")); document.Internals.AddObject(content); page.Elements["/Contents"] = content.Reference;
                document.Outlines.Add("Classroom", page, true);
            }
        }
        document.Save(path);
    }
    private static async Task<CanvasBitmap> BitmapAsync(byte[] bytes, CanvasDevice device)
    {
        using var memory = new InMemoryRandomAccessStream(); using var writer = new DataWriter(memory); writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); memory.Seek(0);
        return await CanvasBitmap.LoadAsync(device, memory, 96);
    }
    private static async Task ScreenshotAsync(Window window, string path)
    {
        var frame = (Controls.Controls.WindowFrame)window.Content;
        var backdrop = frame.Background;
        frame.Background = new SolidColorBrush(frame.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        var bitmap = new RenderTargetBitmap();
        try { await bitmap.RenderAsync(frame); }
        finally { frame.Background = backdrop; }
        var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length]; DataReader.FromBuffer(buffer).ReadBytes(bytes);
        using var memory = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes); await encoder.FlushAsync();
        memory.Seek(0); using var output = File.Create(path); await memory.AsStreamForRead().CopyToAsync(output);
    }

    private static async Task<byte[]> PixelsAsync(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync(); var bytes = new byte[buffer.Length]; DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    private static T FindVisual<T>(DependencyObject root, string name) where T : DependencyObject
    {
        if (root is T element && AutomationProperties.GetName(element) == name) return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, index), name) is { } found) return found;
        return null;
    }
}
