using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Features.Board;
using InkCanvas.Features.Tools;
using InkCanvas.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

using BoardPageThumbnail = InkCanvas.App.Views.BoardPageThumbnail;

namespace InkCanvas.App.Diagnostics;

internal static class BoardManagementRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        var board = services.Board;
        var attributes = new StrokeAttributes { TaperKind = TaperKind.None, BaseWidth = 8, Color = InkColor.Opaque(30, 100, 220) };
        using (var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 40, 30, 96))
        {
            using (var drawing = bitmap.CreateDrawingSession()) drawing.Clear(Microsoft.UI.Colors.Orange);
            using var stream = new InMemoryRandomAccessStream(); await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png); stream.Seek(0);
            var data = new byte[(int)stream.Size]; using var reader = new DataReader(stream); await reader.LoadAsync((uint)data.Length); reader.ReadBytes(data);
            for (int i = 0; i < 1000; i++)
            {
                if (i > 0) board.NextPage();
                board.Background = (BoardBackground)(i % 4);
                if (i < 12)
                {
                    board.Current.Document.AddRange(ShapeGenerator.Generate((i % 3) switch { 0 => ShapeKind.Triangle, 1 => ShapeKind.Ellipse, _ => ShapeKind.Rectangle }, new(300, 120), new(900, 600), attributes));
                    board.AddImage(new() { Name = "Preview " + i, Data = data, Left = 100, Top = 200, Width = 160, Height = 120 });
                    if (i == 8) board.AddMedia(new() { Name = "Video poster", Data = new byte[] { 1 }, PosterData = data,
                        Left = 1100, Top = 400, Width = 320, Height = 180 });
                }
            }
        }
        board.GoTo(0);
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        try
        {
            shell.ToggleBoard(); await Task.Delay(150);
            var window = shell.OpenWindows.OfType<BoardWindow>().Single();
            var root = (FrameworkElement)window.Content;
            var view = board.Current.ViewTransform;
            var open = Descendants(root).OfType<Button>().First(button => AutomationProperties.GetName(button) == "页面管理与总览");
            Invoke(open); await Task.Delay(1500);
            var navigator = window.PageNavigator;
            for (int attempt = 0; attempt < 60 && navigator.CachedThumbnailCount == 0; attempt++) await Task.Delay(50);
            Check("page button opens the 1000-page navigator", window.PagePaneOpen && navigator.ItemCount == 1000);
            Check("opening sidebar preserves the canvas transform", board.Current.ViewTransform == view);
            var sidebarBounds = window.PagePaneSurface.TransformToVisual(root).TransformBounds(new(0, 0,
                window.PagePaneSurface.ActualWidth, window.PagePaneSurface.ActualHeight));
            Check("page manager is an inset tall surface above the page controls", sidebarBounds.Left >= 4
                && sidebarBounds.Bottom < root.ActualHeight - 50 && sidebarBounds.Height > root.ActualHeight * .6);
            Check("page manager uses the board native Mica backdrop", window.SystemBackdrop is MicaBackdrop);
            Check("rename and delete are in thumbnail context menus", !Descendants(navigator).OfType<TouchIconButton>()
                .Any(button => button.Label is "重命名" or "删除"));
            Check("only a bounded visible subset has decoded thumbnails", navigator.CachedThumbnailCount > 0 && navigator.CachedThumbnailCount <= 24,
                new { cached = navigator.CachedThumbnailCount, width = navigator.ActiveView.ActualWidth, height = navigator.ActiveView.ActualHeight });
            Check("touch and mouse reordering are enabled", navigator.ActiveView.CanDragItems && navigator.ActiveView.CanReorderItems
                && navigator.ActiveView.AllowDrop && navigator.ActiveView.IsSwipeEnabled);
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "page-sidebar.png"));
            await SaveDesktop("page-sidebar-desktop.png");
            int beforeOutside = board.Current.Document.Count;
            var bounds = WindowChrome.GetScreenBounds(window, root);
            Check("native pointer can reach the board outside the sidebar", SetCursorPos(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(30);
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(300);
            Check("clicking outside dismisses the sidebar without drawing", !window.PagePaneOpen && board.Current.Document.Count == beforeOutside);
            var rightPages = Descendants(root).OfType<Button>().Last(button => AutomationProperties.GetName(button) == "页面管理与总览");
            Invoke(rightPages); await Task.Delay(350);
            var rightBounds = window.PagePaneSurface.TransformToVisual(root).TransformBounds(new(0, 0,
                window.PagePaneSurface.ActualWidth, window.PagePaneSurface.ActualHeight));
            Check("right page controls open the matching sidebar", window.PagePaneOpen && rightBounds.Right <= root.ActualWidth - 4
                && rightBounds.Left > root.ActualWidth / 2);
            navigator.ActiveView.SelectedIndex = 2; await Task.Delay(300);
            Check("selecting a thumbnail navigates to the matching paper", board.CurrentIndex == 2 && board.Background == BoardBackground.Grid);
            var current = board.Current;
            var items = (ObservableCollection<InkCanvas.App.Views.BoardPageThumbnail>)navigator.ActiveView.ItemsSource;
            var moved = items[0]; items.Move(0, 4);
            Check("native reorder completion moves the page and retains the current controller", navigator.CompleteReorder(moved)
                && ReferenceEquals(board.Pages[4], moved.Controller) && ReferenceEquals(board.Current, current));
            var duplicate = Descendants(navigator).OfType<TouchIconButton>().Single(button => button.Label == "复制");
            Invoke(duplicate); await Task.Delay(200);
            Check("duplicate action copies a full independent page", board.PageCount == 1001 && board.Current.Document.Count == current.Document.Count
                && board.CurrentImages.Count == 1 && !ReferenceEquals(board.Current, current));
            navigator.SetOverview(true); await Task.Delay(1800);
            Check("overview changes to a virtualized grid", navigator.ActiveView is GridView && navigator.ItemCount == 1001);
            Check("overview covers the full board", Math.Abs(window.PagePaneSurface.ActualWidth - root.ActualWidth) <= 1
                && Math.Abs(window.PagePaneSurface.ActualHeight - root.ActualHeight) <= 1);
            Check("overview keeps a bounded thumbnail cache", navigator.CachedThumbnailCount > 0 && navigator.CachedThumbnailCount <= 96);
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "page-overview.png"));
            await SaveDesktop("page-overview-desktop.png");
            var jump = Descendants(navigator).OfType<NumberBox>().Single();
            Check("page navigation accepts all page indices", jump.Maximum == 1001);
            jump.Value = 1000;
            Invoke(Descendants(navigator).OfType<TouchIconButton>().Single(button => button.Label == "跳转")); await Task.Delay(500);
            Check("overview navigation works beyond 99 pages", board.CurrentIndex == 999);
            navigator.SetOverview(false); await Task.Delay(300);
            window.SetPagePaneOpen(false); await Task.Delay(50);
            Check("closing page manager releases decoded thumbnail images", !window.PagePaneOpen && navigator.CachedThumbnailCount == 0);
            window.SetPagePaneOpen(true); await Task.Delay(500);
            Check("reopening retains the page order and current page", navigator.ItemCount == 1001 && board.CurrentIndex == 999);
            string path = Path.Combine(directory, "managed-board.icink");
            int movedPageIndex = board.Pages.ToList().IndexOf(moved.Controller);
            await board.SaveBoardAsync(path); await board.LoadBoardAsync(path); await Task.Delay(600);
            Check("reopen updates page identities and retains the sorted contents", navigator.ItemCount == 1001 && board.Pages.Count == 1001
                && board.GetPageInfo(movedPageIndex).Images[0].Name == "Preview 0");
            await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, "page-reopened.png"));
            var beforeDelete = board.Current;
            InvokePageCommand("删除"); await Task.Delay(100);
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Single();
            Check("deleting a page requires confirmation", board.PageCount == 1001 && board.Pages.Contains(beforeDelete));
            Invoke(Descendants(dialog).OfType<Button>().Single(button => button.Name == "CloseButton")); await Task.Delay(100);
            Check("canceling deletion preserves the page", board.PageCount == 1001 && board.Pages.Contains(beforeDelete));
            InvokePageCommand("删除"); await Task.Delay(100);
            dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Single();
            board.MovePage(0, 4); board.GoTo(20); var activeWhileConfirming = board.Current;
            Invoke(Descendants(dialog).OfType<Button>().Single(button => button.Name == "PrimaryButton"));
            for (int attempt = 0; attempt < 30 && board.PageCount != 1000; attempt++) await Task.Delay(50);
            Check("confirmation deletes its captured page after reordering and navigation", board.PageCount == 1000 && !board.Pages.Contains(beforeDelete)
                && ReferenceEquals(board.Current, activeWhileConfirming), new { board.PageCount, capturedStillPresent = board.Pages.Contains(beforeDelete),
                    activePreserved = ReferenceEquals(board.Current, activeWhileConfirming), board.CurrentIndex });

            void InvokePageCommand(string text)
            {
                var container = (SelectorItem)navigator.ActiveView.ContainerFromItem(navigator.ActiveView.SelectedItem);
                var menu = (MenuFlyout)container.ContextFlyout;
                var command = menu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == text);
                ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(command).GetPattern(PatternInterface.Invoke)).Invoke();
            }

            async Task SaveDesktop(string name)
            {
                var position = window.AppWindow.Position;
                var size = window.AppWindow.Size;
                var capture = ScreenshotService.Capture(position.X, position.Y, size.Width, size.Height);
                await ScreenshotService.SavePngAsync(Path.Combine(directory, name), capture.bgra, capture.width, capture.height);
            }
        }
        catch (Exception ex) { checks.Add(new { check = "page manager exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "page-management.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed, object detail = null) => checks.Add(new { check = name, passed, detail });
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
