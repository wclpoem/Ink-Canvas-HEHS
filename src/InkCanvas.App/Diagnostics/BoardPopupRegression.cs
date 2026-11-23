using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class BoardPopupRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        var window = new BoardWindow(services);
        try
        {
            window.Activate();
            await Task.Delay(300);
            var root = (FrameworkElement)window.Content;
            var main = new FloatingBarWindow();
            main.Bar.Settings = services.Settings;
            main.Configure(services.OverlayInk, null);
            WindowChrome.ShowWithoutActivation(main);
            try
            {
                await Task.Delay(100);
                var mainButtons = Descendants(main.Bar).OfType<TouchIconButton>().ToArray();
                checks.Add(new { check = "main toolbar consolidates image and clone entries",
                    passed = mainButtons.All(button => button.Label is not ("插入图片" or "克隆")) });
                var tools = new BarPanelWindow(main, main.Bar, BarPanelKind.More);
                WindowChrome.ShowWithoutActivation(tools);
                try
                {
                    await Task.Delay(100);
                    var insertion = await MoreMenuRegression.OpenInsertionMenu(tools.Content);
                    var imageButtons = Descendants(insertion).OfType<TouchIconButton>().Where(button => button.Label == "插入图片").ToArray();
                    checks.Add(new { check = "image insertion remains usable in tools menu",
                        passed = imageButtons.Length == 1 && imageButtons[0].IsEnabled && imageButtons[0].Opacity == 1 });
                }
                finally { tools.Close(); }
            }
            finally { main.Close(); }

            var clearButton = Descendants(root).OfType<Button>()
                .Single(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "清屏"));
            var eraserButton = Descendants(root).OfType<Button>()
                .Single(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "橡皮"));
            checks.Add(new { check = "board clear is a separate toolbar action",
                passed = clearButton.Parent == eraserButton.Parent && clearButton.Width == eraserButton.Width
                    && clearButton.Height == eraserButton.Height && AutomationProperties.GetItemStatus(clearButton) != "已选中" });
            var stroke = new InkStroke(new());
            stroke.AddPoint(100, 100); stroke.AddPoint(180, 150); stroke.Complete();
            var original = stroke.Points.ToArray();
            window.CurrentController.Document.Add(stroke);
            window.CurrentController.Tool = InkTool.Select;
            Invoke(clearButton);
            await Task.Delay(100);
            checks.Add(new { check = "toolbar clear skips confirmation",
                passed = !VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                    .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Any() });
            checks.Add(new { check = "toolbar clear uses current page workflow",
                passed = window.CurrentController.Document.Count == 0 && window.CurrentController.Tool == InkTool.Pen
                    && window.CurrentController.History.CanUndo });
            window.CurrentController.Undo();
            checks.Add(new { check = "toolbar clear can be undone",
                passed = window.CurrentController.Document.Strokes.Single().Points.SequenceEqual(original) });
            window.CurrentController.Document.Clear();
            window.CurrentController.History.Clear();
            foreach (var background in new[] { BoardBackground.White, BoardBackground.Black })
            foreach (double scale in new[] { 50d, 100d, 150d })
            {
                services.Board.SetBackground(background);
                services.Settings.Appearance.BlackboardScale = scale;
                window.RefreshUiScale();
                await Task.Delay(80);
                var docks = window.DockSurfaces.Select(surface => surface.TransformToVisual(root).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, surface.ActualWidth, surface.ActualHeight))).ToArray();
                bool dockFits = docks.All(bounds => bounds.Left >= -1 && bounds.Top >= -1
                    && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1);
                bool separateDocks = docks.SelectMany((bounds, index) => docks.Skip(index + 1).Select(other =>
                    bounds.Right <= other.Left + 1 || other.Right <= bounds.Left + 1
                    || bounds.Bottom <= other.Top + 1 || other.Bottom <= bounds.Top + 1)).All(separate => separate);
                checks.Add(new { check = "board toolbar fits dedicated clear action", background = background.ToString(), scale,
                    passed = dockFits && separateDocks });
                if (scale == 100) await BoardWindowRegression.SaveVisual(window.DockSurfaces[1], Path.Combine(directory, $"toolbar-{background}.png"));
                var more = Descendants(root).OfType<Button>().First(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "更多"));
                Invoke(more);
                await Task.Delay(200);
                var popup = MorePanel();
                var presenter = (FrameworkElement)popup.Content;
                checks.Add(new { check = "more menu uses dedicated clear action",
                    passed = !Descendants(presenter).OfType<TextBlock>().Any(text => text.Text == "清屏") });
                string name = $"{background}-{scale}";
                CheckSurface(popup, name);
                var view = (Viewbox)((Border)popup.Content).Child;
                bool previewAvailable = await BoardWindowRegression.SaveVisual((FrameworkElement)view.Child, Path.Combine(directory, $"{name}.png"));
                checks.Add(new { check = "more menu preview", name, available = previewAvailable });
                services.Settings.Appearance.BlackboardScale = scale == 150 ? 50 : 150;
                window.RefreshUiScale();
                await Task.Delay(120);
                CheckSurface(popup, name + " after scale change");
                Invoke(more);
                await Task.Delay(80);
            }
            // Changing the owner size while a menu is open must update its scale and placement.
            services.Board.SetBackground(BoardBackground.White);
            services.Settings.Appearance.BlackboardScale = 150;
            window.RefreshUiScale();
            var moreButton = Descendants(root).OfType<Button>().First(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "更多"));
            Invoke(moreButton);
            await Task.Delay(120);
            var active = MorePanel();
            var windowMode = Descendants(active.Content).OfType<Button>()
                .Single(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "窗口模式"));
            Invoke(windowMode);
            await Task.Delay(120);
            root = (FrameworkElement)window.Content;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 650));
            await Task.Delay(120);
            Invoke(moreButton);
            await Task.Delay(120);
            active = MorePanel();
            CheckSurface(active, "small window");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 760));
            await Task.Delay(120);
            // WinUI may dismiss an anchored flyout when its owner resizes. Reopening must
            // measure the new viewport instead of reusing dimensions from the closed popup.
            active = window.ToolPanels.SingleOrDefault(item => Descendants(item.Content).OfType<TextBlock>().Any(text => text.Text == "更多功能"));
            if (active is null)
            {
                Invoke(moreButton);
                await Task.Delay(120);
                active = MorePanel();
            }
            CheckSurface(active, "menu fits after owner resize");
            var paper = Descendants(active.Content).OfType<Button>()
                .Single(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "画布纸型"));
            Invoke(paper);
            await Task.Delay(120);
            bool submenu = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .Any(item => Descendants(item.Child).OfType<MenuFlyoutPresenter>().Any());
            checks.Add(new { check = "more menu opens paper submenu", passed = submenu });
        }
        catch (Exception ex) { checks.Add(new { check = "board popup exception", passed = false, error = ex.ToString() }); }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(directory, "board-popup.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            Application.Current.Exit();
        }

        ToolOptionsWindow MorePanel() => window.ToolPanels.Single(item => Descendants(item.Content).OfType<TextBlock>().Any(text => text.Text == "更多功能"));

        void CheckSurface(ToolOptionsWindow panel, string name)
        {
            var presenter = (Border)panel.Content;
            var view = (Viewbox)presenter.Child;
            var surface = (Border)view.Child;
            var bounds = WindowChrome.GetScreenBounds(panel, surface);
            var outer = WindowChrome.GetScreenBounds(panel, presenter);
            bool exactSize = outer.Width > 0 && outer.Height > 0 && Math.Abs(outer.X - bounds.X) <= 1 && Math.Abs(outer.Y - bounds.Y) <= 1
                && Math.Abs(outer.Width - bounds.Width) <= 2 && Math.Abs(outer.Height - bounds.Height) <= 2;
            bool buttonsFit = Descendants(surface).OfType<Button>().All(button =>
            {
                var rect = button.TransformToVisual(presenter).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                return rect.Left >= -1 && rect.Top >= -1 && rect.Right <= presenter.ActualWidth + 1 && rect.Bottom <= presenter.ActualHeight + 1;
            });
            bool transparent = presenter.Background is SolidColorBrush brush && brush.Color.A == 0;
            checks.Add(new { check = "more presenter fits its painted surface", name, exactSize, buttonsFit, transparent,
                passed = exactSize && buttonsFit && transparent, bounds, presenterBounds = outer });
        }
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
