using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Board;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace InkCanvas.App.Diagnostics;

internal static class DesktopVideoRegression
{
    public static async Task Run(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.IsAutoClearWhenExitingWritingMode = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        try
        {
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            var tools = Descendants(bar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "工具");
            ((IInvokeProvider)new ButtonAutomationPeer(tools).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(150);
            var insertion = await MoreMenuRegression.OpenInsertionMenu(bar.GetOpenPanel(BarPanelKind.More).Content);
            var videoButton = Descendants(insertion).OfType<TouchIconButton>().Single(button => button.Label == "插入视频");
            Check("desktop video command is visible and enabled", videoButton?.IsEnabled == true && videoButton.Glyph == Glyphs.Video);
            var menu = bar.GetOpenPanel(BarPanelKind.More);
            await BoardWindowRegression.SaveVisual((FrameworkElement)menu.Content, Path.Combine(directory, "video-menu.png"));
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync("video.mp4", CreationCollisionOption.ReplaceExisting);
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(Microsoft.UI.Colors.DodgerBlue, TimeSpan.FromSeconds(4)));
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga); profile.Video.Width = 640; profile.Video.Height = 360;
            var result = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
            Check("native video fixture encodes", result == TranscodeFailureReason.None);
            if (result != TranscodeFailureReason.None) return;
            ((IInvokeProvider)new ButtonAutomationPeer(videoButton).GetPattern(PatternInterface.Invoke)).Invoke();
            var picker = await ImageImportRegression.FindPickerAsync("插入视频");
            Check("video picker leaves the annotation window enabled", IsWindowEnabled(shell.Overlay.Window.Handle));
            await ImageImportRegression.ChooseFileAsync(picker, file.Path);
            await WaitUntil(() => services.ScreenImages.CurrentMedia.Count > 0);
            var asset = services.ScreenImages.CurrentMedia.Single();
            asset.Left = 160; asset.Top = 160; asset.Width = 480; asset.Height = 270;
            services.ScreenImages.NotifyMediaPlacement(asset); await Task.Delay(180);
            var window = shell.DesktopVideos.Single();
            await WaitUntil(() => !window.MediaLayer.IsLoading(asset));
            var player = window.MediaLayer.GetPlayer(asset);
            Check("desktop video opens one native decoder and selects its object", player?.PlaybackSession.NaturalVideoWidth == 640
                && services.OverlayInk.SelectedElements.Single().Id == asset.Id && window.AppWindow.IsVisible);
            Check("video surface does not activate the presentation", NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_NOACTIVATE));
            Check("desktop ink stays above the video playback surface", NativeMethods.GetWindow(WindowChrome.GetHwnd(window), 3) == shell.Overlay.Window.Handle);
            player.Play(); shell.ToggleClickThrough(); await Task.Delay(200);
            Check("mouse mode exposes video transport while ink is click-through", shell.Overlay.ClickThrough && window.AppWindow.IsVisible
                && window.MediaLayer.GetViewElement(asset) is Grid view && view.Children.OfType<MediaPlayerElement>().Single().AreTransportControlsEnabled);
            var element = services.ScreenImages.GetSelectionElements().Single();
            var center = element.Frame.Center(Matrix3x2.Identity);
            var matrix = Matrix3x2.CreateTranslation(-center) * Matrix3x2.CreateRotation(.5f) * Matrix3x2.CreateScale(1.1f)
                * Matrix3x2.CreateTranslation(center + new Vector2(80, 40));
            element.Transform(matrix); await Task.Delay(120);
            Check("moving and rotating retain the playing decoder", ReferenceEquals(player, window.MediaLayer.GetPlayer(asset)));
            nint region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                int kind = GetWindowRgn(WindowChrome.GetHwnd(window), region);
                Check("rotated native surface clips its unused corner pixels", kind == 3 && !PtInRegion(region, 2, 2));
            }
            finally { NativeMethods.DeleteObject(region); }
            string path = Path.Combine(directory, "desktop-video.icink");
            await services.ScreenImages.SavePageAsync(path);
            var apply = await services.PrepareInkOpenAsync(path, services.ScreenImages); apply(); await Task.Delay(180);
            var loaded = services.ScreenImages.CurrentMedia.Single(); window = shell.DesktopVideos.Single();
            await WaitUntil(() => !window.MediaLayer.IsLoading(loaded));
            Check("normal open workflow restores playable desktop media and geometry", loaded.Data.AsSpan().SequenceEqual(asset.Data)
                && loaded.Transform.SequenceEqual(asset.Transform) && window.MediaLayer.GetPlayer(loaded)?.PlaybackSession.NaturalVideoWidth == 640);
            var copy = (BoardSelectionElement)services.ScreenImages.GetSelectionElements().Single().Duplicate(); copy.SetPresent(true);
            await Task.Delay(180);
            Check("desktop clone shares media bytes and owns a separate player", shell.DesktopVideos.Count == 2 && ReferenceEquals(copy.Media.Data, loaded.Data));
            copy.SetPresent(false); await Task.Delay(100);
            Check("deleting a desktop clone releases its player window", shell.DesktopVideos.Count == 1);
            shell.ToggleBoard(); await Task.Delay(150);
            Check("entering board hides and pauses desktop video", !window.AppWindow.IsVisible);
            shell.ToggleBoard(); await Task.Delay(150);
            Check("returning to desktop restores the video surface", window.AppWindow.IsVisible);
            services.ScreenImages.RemoveMedia(loaded); await Task.Delay(120);
            Check("removing the final video releases every native player", shell.DesktopVideos.Count == 0);
        }
        catch (Exception ex) { checks.Add(new { check = "desktop video exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "desktop-video.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string check, bool passed) => checks.Add(new { check, passed });
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (int index = 0; index < 100 && !done(); index++) await Task.Delay(50);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
}
