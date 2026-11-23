using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Numerics;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using InkCanvas.Features.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.App.Diagnostics;

internal static class PptPreviewRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        string pagesArgument = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--pptpreviewpages=", StringComparison.Ordinal));
        int pageCount = pagesArgument is null ? 48 : int.Parse(pagesArgument["--pptpreviewpages=".Length..]);
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.PowerPoint.IsNotifyHiddenPage = pageCount > 200;
        services.Settings.PowerPoint.IsNotifyPreviousPage = false;
        services.Settings.PowerPoint.IsNotifyAutoPlayPresentation = false;
        services.Settings.PowerPoint.IsAutoSaveScreenShotInPowerPoint = false;
        services.Settings.PowerPoint.IsAutoSaveStrokesInPowerPoint = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(Path.GetFullPath(directory), "ink");
        using var shell = new ShellController(services);
        object appObject = null, presentation = null, show = null;
        var owned = new List<object>();
        try
        {
            shell.Start(); shell.StartInClickThrough();
            await Task.Delay(400);
            appObject = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: true));
            dynamic app = appObject;
            dynamic running = Own((object)app.SlideShowWindows);
            if ((int)running.Count > 0) throw new InvalidOperationException("An existing slideshow is running.");
            dynamic presentations = Own((object)app.Presentations);
            presentation = Own((object)presentations.Add(-1));
            dynamic document = presentation;
            dynamic slides = Own((object)document.Slides);
            var creation = Stopwatch.StartNew();
            for (int index = 1; index <= pageCount; index++)
            {
                dynamic slide = Own((object)slides.Add(index, 12));
                dynamic shapes = Own((object)slide.Shapes);
                dynamic box = Own((object)shapes.AddShape(1, 80, 80, 300, 160));
                dynamic fill = Own((object)box.Fill);
                dynamic color = Own((object)fill.ForeColor);
                color.RGB = index % 2 == 0 ? 0xDDBB77 : 0x77BBDD;
                if (index % 100 == 0)
                {
                    File.AppendAllText(Path.Combine(directory, "progress.log"), $"Created {index}/{pageCount} slides{Environment.NewLine}");
                    await Task.Delay(1);
                }
            }
            Check("large deck created", (int)slides.Count == pageCount, new { pageCount, elapsedMs = creation.ElapsedMilliseconds });
            document.SaveAs(Path.Combine(Path.GetFullPath(directory), "preview-test.pptx"), 24);
            dynamic settings = Own((object)document.SlideShowSettings);
            settings.AdvanceMode = 1;
            foreach (int showType in new[] { 4, 1 })
            {
                settings.ShowType = showType;
                dynamic documentWindows = Own((object)document.Windows);
                dynamic documentWindow = Own((object)documentWindows[1]);
                documentWindow.Activate();
                await Task.Delay(500);
                for (int attempt = 0; show is null; attempt++)
                {
                    try { show = Own((object)settings.Run()); }
                    catch (COMException ex) when (ex.HResult is unchecked((int)0x80004005) or unchecked((int)0x80010001))
                    {
                        if (attempt < 8) { await Task.Delay(300); continue; }
                        checks.Add(new { check = "slideshow availability", available = false, showType, error = ex.Message });
                        break;
                    }
                }
                if (show is null) continue;
                await Task.Delay(300);
                services.Ppt.TryAttach();
                services.Settings.PowerPoint.PowerPointSupport = true;
                await Until(() => services.PresentationActive && services.Ppt.Snapshot.Slide?.Index == 1);
                var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
                var navigations = shell.OpenWindows.OfType<PptNavigationWindow>().ToArray();
                Check("four navigation tools created", navigations.Length == 4, new { showType });
                var live = services.OverlayInk;
                AddInk(200);
                shell.PptGotoSlide(2);
                await Until(() => services.Ppt.Snapshot.Slide?.Index == 2);
                AddInk(320);
                foreach (var navigation in navigations)
                {
                    foreach (int scale in new[] { 50, 100, 150 })
                    {
                        services.Settings.Appearance.FloatingBarScale = scale;
                        bar.Reposition(services.Settings.Appearance.FloatingBarBottomMargin, scale, recenter: false);
                        services.ApplySettings();
                        await Task.Delay(80);
                        var card = navigation.NavigationCard;
                        var bounds = WindowChrome.GetScreenBounds(navigation, card);
                        bool vertical = navigation.Placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight;
                        var toolbarCard = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
                        Check("navigation shares toolbar height and corners", Math.Abs((vertical ? bounds.Width : bounds.Height) - toolbarCard.Height) <= 2
                            && Math.Abs(card.CornerRadius.TopLeft - FloatingBar.ChromeCornerRadius) < .01
                            && Math.Abs(card.BorderThickness.Left - FloatingBar.ChromeBorderThickness) < .01,
                            new { showType, scale, navigation.Placement, bounds, toolbarCard, card.ActualWidth, card.ActualHeight,
                                effectiveScale = bar.EffectiveScale });
                    }
                    services.Settings.Appearance.FloatingBarScale = 100;
                    bar.Reposition(services.Settings.Appearance.FloatingBarBottomMargin, 100, recenter: false);
                    services.ApplySettings();
                    var counter = Descendants(navigation.NavigationCard).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "幻灯片页码");
                    var opening = Stopwatch.StartNew();
                    Invoke(counter);
                    double openMs = opening.Elapsed.TotalMilliseconds;
                    await Until(() => navigation.Preview?.CachedThumbnailCount >= 2);
                    Check("counter opens own bounded slide preview", navigation.PreviewOpen && navigation.Preview.ItemCount == pageCount
                        && navigation.Preview.CachedThumbnailCount <= 24 && Math.Abs(navigation.PreviewSurface.ActualWidth - BoardPageNavigator.SidebarWidth) <= 1,
                        new { showType, navigation.Placement, openMs, cached = navigation.Preview.CachedThumbnailCount });
                    Check("preview export preserves current slide and ink", services.Ppt.Snapshot.Slide.Index == 2 && live.Document.Count == 1,
                        new { showType, navigation.Placement });
                    if (navigation.Placement == PptNavigationPlacement.BottomLeft)
                    {
                        var first = (PptPageThumbnail)navigation.Preview.List.Items[0];
                        var second = (PptPageThumbnail)navigation.Preview.List.Items[1];
                        await Until(() => first.Preview is not null && second.Preview is not null);
                        Check("stored slide ink is included in preview", await HasRedPixels(navigation.Preview, first.Preview));
                        Check("current unsaved ink is included in preview", await HasRedPixels(navigation.Preview, second.Preview));
                        await BoardWindowRegression.SaveVisual((FrameworkElement)navigation.Content, Path.Combine(directory, $"preview-{showType}.png"));
                        Check("preview work is limited to visible slides", navigation.Preview.ExportCount < 24,
                            new { exports = navigation.Preview.ExportCount });
                        navigation.Preview.SetOverview(true);
                        await Task.Delay(600);
                        var area = bar.GetPlacementArea();
                        var overviewBounds = WindowChrome.GetScreenBounds(navigation, navigation.PreviewSurface);
                        Check("PPT overview covers the presentation area", navigation.Preview.ActiveView is GridView
                            && Math.Abs(overviewBounds.Width - area.Width) <= 2 && Math.Abs(overviewBounds.Height - area.Height) <= 2,
                            new { overviewBounds, area });
                        await BoardWindowRegression.SaveVisual((FrameworkElement)navigation.Content, Path.Combine(directory, $"overview-{showType}.png"));
                        navigation.Preview.SetOverview(false);
                        if (pageCount > 200)
                        {
                            int exportsBeforeScroll = navigation.Preview.ExportCount;
                            for (int step = 1; step <= 12; step++)
                            {
                                int index = (pageCount - 1) * step / 12;
                                navigation.Preview.List.ScrollIntoView(navigation.Preview.List.Items[index]);
                                await Task.Delay(30);
                            }
                            var last = (PptPageThumbnail)navigation.Preview.List.Items[pageCount - 1];
                            await Until(() => last.Preview is not null);
                            Check("rapid scrolling loads final visible page on demand", last.Slide.Index == pageCount && last.Slide.SlideId > 0
                                && navigation.Preview.ExportCount - exportsBeforeScroll < 24,
                                new { pageCount, exports = navigation.Preview.ExportCount - exportsBeforeScroll });
                            Check("large preview caches stay bounded", navigation.Preview.CachedSourceCount <= 32
                                && navigation.Preview.CachedSourceBytes <= 16 * 1024 * 1024 && navigation.Preview.CachedThumbnailCount <= 24,
                                new { navigation.Preview.CachedSourceCount, navigation.Preview.CachedSourceBytes, navigation.Preview.CachedThumbnailCount });
                            Check("far page preview preserves active slide and ink", services.Ppt.Snapshot.Slide.Index == 2 && live.Document.Count == 1);
                        }
                    }
                    Invoke(counter);
                    await Task.Delay(300);
                    Check("closing preview releases decoded thumbnails", !navigation.PreviewOpen && navigation.Preview.CachedThumbnailCount == 0);
                }
                var firstNavigation = navigations.Single(window => window.Placement == PptNavigationPlacement.BottomLeft);
                firstNavigation.SetPreviewOpen(true);
                await Until(() => firstNavigation.Preview.CachedThumbnailCount >= 2);
                firstNavigation.Preview.List.SelectedIndex = 0;
                await Until(() => services.Ppt.Snapshot.Slide?.Index == 1 && !firstNavigation.PreviewOpen);
                Check("clicking thumbnail jumps to the real slide and restores its ink", live.Document.Count == 1);
                firstNavigation.SetPreviewOpen(true);
                await Until(() => firstNavigation.Preview.CachedThumbnailCount >= 2);
                var display = bar.GetPlacementArea();
                bool nativeAvailable = SetCursorPos(display.X + display.Width / 2, display.Y + display.Height / 2);
                if (nativeAvailable)
                {
                    MouseEvent(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(30); MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
                    await Until(() => !firstNavigation.PreviewOpen);
                    Check("outside click dismisses previews", !firstNavigation.PreviewOpen);
                }
                else checks.Add(new { check = "outside click desktop access", available = false });
                firstNavigation.SetPreviewOpen(false);
                ((dynamic)show).View.Exit(); show = null;
                await Until(() => !services.PresentationActive);
                services.Settings.PowerPoint.PowerPointSupport = false;
                services.Ppt.Detach();
            }
        }
        catch (Exception ex) { checks.Add(new { check = "PPT preview regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            try { if (show is not null) ((dynamic)show).View.Exit(); } catch { }
            services.Settings.PowerPoint.PowerPointSupport = false;
            services.Ppt.Detach();
            try { if (presentation is not null) { ((dynamic)presentation).Saved = -1; ((dynamic)presentation).Close(); } } catch { }
            for (int index = owned.Count - 1; index >= 0; index--) try { Marshal.ReleaseComObject(owned[index]); } catch { }
            if (appObject is not null) Marshal.ReleaseComObject(appObject);
            await File.WriteAllTextAsync(Path.Combine(directory, "ppt-preview.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        object Own(object value) { owned.Add(value); return value; }
        void AddInk(float y)
        {
            var snapshot = services.Ppt.Snapshot;
            var info = snapshot.Presentation;
            var area = shell.OpenWindows.OfType<FloatingBarWindow>().Single().GetPlacementArea();
            float scale = (float)Math.Min(area.Width / info.SlideWidth, area.Height / info.SlideHeight);
            float left = (float)(area.X + (area.Width - info.SlideWidth * scale) / 2);
            float top = (float)(area.Y + (area.Height - info.SlideHeight * scale) / 2);
            var worldToSlide = services.OverlayInk.ViewTransform * Matrix3x2.CreateScale((float)shell.Overlay.Window.DpiScale)
                * Matrix3x2.CreateTranslation(shell.Overlay.Window.X - left, shell.Overlay.Window.Y - top) * Matrix3x2.CreateScale(1 / scale);
            Matrix3x2.Invert(worldToSlide, out var slideToWorld);
            var stroke = Line(100, y, 420, y); stroke.Transform(slideToWorld);
            services.OverlayInk.Document.Add(stroke);
        }
        void Check(string name, bool passed, object detail = null)
        {
            checks.Add(new { check = name, passed, detail });
            File.AppendAllText(Path.Combine(directory, "progress.log"), name + " " + passed + Environment.NewLine);
        }
    }

    private static InkStroke Line(float x, float y, float right, float bottom)
    {
        var stroke = new InkStroke(new StrokeAttributes { Color = InkColor.Opaque(230, 20, 20), BaseWidth = 20, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y); stroke.AddPoint(right, bottom); stroke.Complete(); return stroke;
    }

    private static async Task<bool> HasRedPixels(FrameworkElement root, ImageSource source)
    {
        var image = Descendants(root).OfType<Image>().Single(item => ReferenceEquals(item.Source, source));
        for (int attempt = 0; attempt < 25; attempt++)
        {
            // ImageSource assignment can precede the frame that uploads its decoded pixels.
            await Task.Delay(40);
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(image);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(buffer);
            var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
            for (int index = 0; index < pixels.Length; index += 4)
                if (pixels[index + 2] > 170 && pixels[index + 1] < 80 && pixels[index] < 80) return true;
        }
        return false;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 150; attempt++) { if (condition()) return; await Task.Delay(80); }
        throw new TimeoutException("PPT preview condition did not become true.");
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
