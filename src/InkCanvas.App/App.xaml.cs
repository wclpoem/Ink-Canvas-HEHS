using InkCanvas.App.Services;
using InkCanvas.Platform.Ai;
using Microsoft.UI.Xaml;

namespace InkCanvas.App;

public partial class App : Application
{
    private ShellController _shell;
    private Diagnostics.ActivationHost _activationHost;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().ShutdownCompleted += (_, _) =>
            Core.Diagnostics.FileLog.WriteLogToFile("Application dispatcher shutdown completed");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Core.Diagnostics.FileLog.WriteLogToFile("ProcessExit");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Diagnostics.FileLog.WriteLogToFile($"AppDomain unhandled: {e.ExceptionObject}", Core.Diagnostics.LogType.Error);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        AiConfig.ApplyOverrides(
            ReadOption(commandLineArgs, "--ai-endpoint="),
            ReadOption(commandLineArgs, "--ai-model="),
            ReadOption(commandLineArgs, "--ai-timeout-ms="),
            ReadOption(commandLineArgs, "--ai-image-timeout-ms="));
        Core.Diagnostics.FileLog.WriteLogToFile(
            "[AI] " + AiConfig.ReadFromEnvironment().Summarize(), Core.Diagnostics.LogType.Info);

        static string ReadOption(string[] args, string prefix)
        {
            string match = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return match is null ? "" : match[prefix.Length..];
        }

        if (Environment.GetCommandLineArgs().Contains("--register-file-associations", StringComparer.OrdinalIgnoreCase))
        {
            try { Platform.System.FileAssociationService.Register(Environment.ProcessPath); }
            catch (Exception error) { Core.Diagnostics.FileLog.WriteLogToFile("文件关联注册失败 " + error.Message, Core.Diagnostics.LogType.Error); }
            Exit(); return;
        }
        var msiCheck = Environment.GetCommandLineArgs().FirstOrDefault(argument => argument.StartsWith("--msicheck=", StringComparison.Ordinal));
        if (msiCheck is not null)
        {
            string package = Environment.GetCommandLineArgs().Single(argument => argument.StartsWith("--msipackage=", StringComparison.Ordinal))["--msipackage=".Length..];
            _ = Diagnostics.MsiRegression.Run(msiCheck["--msicheck=".Length..], package); return;
        }
        var previewWorker = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--pptpreviewworker=", StringComparison.Ordinal));
        if (previewWorker is not null)
        {
            int parent = int.Parse(Environment.GetCommandLineArgs().Single(arg => arg.StartsWith("--pptpreviewparent=", StringComparison.Ordinal))["--pptpreviewparent=".Length..]);
            string directory = Environment.GetCommandLineArgs().Single(arg => arg.StartsWith("--pptpreviewdirectory=", StringComparison.Ordinal))["--pptpreviewdirectory=".Length..];
            _ = PptPreviewWorker.Run(previewWorker["--pptpreviewworker=".Length..], parent, directory);
            return;
        }
        var moreMenuCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--moremenucheck=", StringComparison.OrdinalIgnoreCase));
        if (moreMenuCheck is not null) { _ = Diagnostics.MoreMenuRegression.Run(moreMenuCheck["--moremenucheck=".Length..]); return; }
        var cloudSyncCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--cloudsynccheck=", StringComparison.OrdinalIgnoreCase));
        if (cloudSyncCheck is not null) { _ = Diagnostics.CloudSyncRegression.Run(cloudSyncCheck["--cloudsynccheck=".Length..]); return; }
        var assistantCompatibility = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--assistantcompatcheck=", StringComparison.OrdinalIgnoreCase));
        if (assistantCompatibility is not null) { _ = Diagnostics.AssistantCompatibilityRegression.Run(assistantCompatibility["--assistantcompatcheck=".Length..]); return; }
        var aiConnectionsCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--aiconnectionscheck=", StringComparison.OrdinalIgnoreCase));
        if (aiConnectionsCheck is not null) { _ = Diagnostics.AiConnectionsRegression.Run(aiConnectionsCheck["--aiconnectionscheck=".Length..]); return; }
        var boardChromeCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--boardchromecheck=", StringComparison.OrdinalIgnoreCase));
        if (boardChromeCheck is not null) { _ = Diagnostics.BoardChromeRegression.Run(boardChromeCheck["--boardchromecheck=".Length..]); return; }
        var moduleResourcesCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--moduleresourcescheck=", StringComparison.OrdinalIgnoreCase));
        if (moduleResourcesCheck is not null) { _ = Diagnostics.ModuleResourcesRegression.Run(moduleResourcesCheck["--moduleresourcescheck=".Length..]); return; }
        var resizeCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--windowresizecheck="));
        if (resizeCheck is not null) { _ = Diagnostics.WindowResizeRegression.Run(resizeCheck["--windowresizecheck=".Length..]); return; }
        var extensionUpdatesCheck = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--extensionupdatescheck=", StringComparison.OrdinalIgnoreCase));
        if (extensionUpdatesCheck is not null) { _ = Diagnostics.ExtensionUpdateRegression.Run(extensionUpdatesCheck["--extensionupdatescheck=".Length..]); return; }
        var countdownCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--countdowncheck=", StringComparison.OrdinalIgnoreCase));
        if (countdownCheck is not null) { _ = Diagnostics.CountdownWindowRegression.Run(countdownCheck["--countdowncheck=".Length..]); return; }
        var iconCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--iconfontcheck=", StringComparison.OrdinalIgnoreCase));
        if (iconCheck is not null) { _ = Diagnostics.IconFontRegression.Run(iconCheck["--iconfontcheck=".Length..]); return; }
        var documentAiCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--documentaicheck=", StringComparison.OrdinalIgnoreCase));
        if (documentAiCheck is not null) { _ = Diagnostics.DocumentAiRegression.Run(documentAiCheck["--documentaicheck=".Length..]); return; }
        var libraryCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--librarycheck=", StringComparison.OrdinalIgnoreCase));
        if (libraryCheck is not null) { _ = Diagnostics.LibraryRegression.Run(libraryCheck["--librarycheck=".Length..]); return; }
        var screenArchiveFixture = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--screenarchivefixture=", StringComparison.OrdinalIgnoreCase));
        if (screenArchiveFixture is not null) { Diagnostics.ScreenArchiveRegression.Fixture(screenArchiveFixture["--screenarchivefixture=".Length..]); return; }
        var screenArchiveCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--screenarchivecheck=", StringComparison.OrdinalIgnoreCase));
        if (screenArchiveCheck is not null) { _ = Diagnostics.ScreenArchiveRegression.Run(screenArchiveCheck["--screenarchivecheck=".Length..]); return; }
        var archiveCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--archivecheck=", StringComparison.OrdinalIgnoreCase));
        if (archiveCheck is not null) { _ = Diagnostics.ArchiveRegression.Run(archiveCheck["--archivecheck=".Length..]); return; }
        var aiPluginCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--aiplugincheck=", StringComparison.OrdinalIgnoreCase));
        if (aiPluginCheck is not null) { _ = Diagnostics.AiPluginRegression.Run(aiPluginCheck["--aiplugincheck=".Length..]); return; }
        var micaCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--micacheck=", StringComparison.OrdinalIgnoreCase));
        if (micaCheck is not null) { _ = Diagnostics.MicaWindowRegression.Run(micaCheck["--micacheck=".Length..]); return; }

        var sideBoardLongCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--sideboardcheck-long=", StringComparison.OrdinalIgnoreCase));
        if (sideBoardLongCheck is not null) { _ = Diagnostics.SideBoardRegression.Run(sideBoardLongCheck["--sideboardcheck-long=".Length..], cycles: 500); return; }
        var sideBoardCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--sideboardcheck=", StringComparison.OrdinalIgnoreCase));
        if (sideBoardCheck is not null) { _ = Diagnostics.SideBoardRegression.Run(sideBoardCheck["--sideboardcheck=".Length..]); return; }

        var aiCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--aicheck=", StringComparison.OrdinalIgnoreCase));
        if (aiCheck is not null) { _ = Diagnostics.AssistantRegression.Run(aiCheck["--aicheck=".Length..]); return; }
        var aiBoardCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--aiboardcheck=", StringComparison.OrdinalIgnoreCase));
        if (aiBoardCheck is not null) { _ = Diagnostics.AiBoardRegression.Run(aiBoardCheck["--aiboardcheck=".Length..]); return; }
        var compatCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--compatcheck=", StringComparison.OrdinalIgnoreCase));
        if (compatCheck is not null) { _ = Diagnostics.CompatibilityRegression.Run(compatCheck["--compatcheck=".Length..]); return; }

        var inkWashCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--inkwashcheck=", StringComparison.OrdinalIgnoreCase));
        if (inkWashCheck is not null) { _ = Diagnostics.InkWashRegression.Run(inkWashCheck["--inkwashcheck=".Length..]); return; }
        var penContrastCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pencontrastcheck=", StringComparison.OrdinalIgnoreCase));
        if (penContrastCheck is not null) { _ = Diagnostics.PenContrastRegression.Run(penContrastCheck["--pencontrastcheck=".Length..]); return; }
        var settingsActivationCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--settingsactivationcheck=", StringComparison.OrdinalIgnoreCase));
        if (settingsActivationCheck is not null) { _ = Diagnostics.SettingsActivationRegression.Run(settingsActivationCheck["--settingsactivationcheck=".Length..]); return; }
        var updateUiCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--updateuicheck=", StringComparison.OrdinalIgnoreCase));
        if (updateUiCheck is not null) { _ = Diagnostics.UpdateUiRegression.Run(updateUiCheck["--updateuicheck=".Length..]); return; }
        var moduleToolbarCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--moduletoolbarcheck=", StringComparison.OrdinalIgnoreCase));
        if (moduleToolbarCheck is not null) { _ = Diagnostics.ModuleToolbarRegression.Run(moduleToolbarCheck["--moduletoolbarcheck=".Length..]); return; }
        var storageCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--storagecheck=", StringComparison.OrdinalIgnoreCase));
        if (storageCheck is not null) { _ = Diagnostics.StorageUpdateRegression.Run(storageCheck["--storagecheck=".Length..]); return; }
        var pptCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pptcheck=", StringComparison.OrdinalIgnoreCase));
        if (pptCheck is not null) { _ = Diagnostics.PptRegression.Run(pptCheck["--pptcheck=".Length..]); return; }
        var pptNavigationCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pptnavlayoutcheck=", StringComparison.OrdinalIgnoreCase));
        var pptPreviewCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pptpreviewcheck=", StringComparison.OrdinalIgnoreCase));
        if (pptPreviewCheck is not null) { _ = Diagnostics.PptPreviewRegression.Run(pptPreviewCheck["--pptpreviewcheck=".Length..]); return; }
        if (pptNavigationCheck is not null) { _ = Diagnostics.PptNavigationLayoutRegression.Run(pptNavigationCheck["--pptnavlayoutcheck=".Length..]); return; }
        var updateSourceCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--updatesourcecheck=", StringComparison.OrdinalIgnoreCase));
        if (updateSourceCheck is not null) { _ = Diagnostics.UpdateSourceRegression.Run(updateSourceCheck["--updatesourcecheck=".Length..]); return; }
        var toolbarAnimationCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--toolbaranimationcheck=", StringComparison.OrdinalIgnoreCase));
        if (toolbarAnimationCheck is not null) { _ = Diagnostics.ToolbarAnimationRegression.Run(toolbarAnimationCheck["--toolbaranimationcheck=".Length..]); return; }
        var lotteryCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--lotterycheck=", StringComparison.OrdinalIgnoreCase));
        if (lotteryCheck is not null) { _ = Diagnostics.LotteryRegression.Run(lotteryCheck["--lotterycheck=".Length..]); return; }
        var selectionTouchCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--selectiontouchcheck=", StringComparison.OrdinalIgnoreCase));
        if (selectionTouchCheck is not null) { _ = Diagnostics.SelectionTouchRegression.Run(selectionTouchCheck["--selectiontouchcheck=".Length..]); return; }
        var themeCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--themecheck=", StringComparison.OrdinalIgnoreCase));
        if (themeCheck is not null) { _ = Diagnostics.ChromeThemeRegression.Run(themeCheck["--themecheck=".Length..]); return; }
        var desktopVideoCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--desktopvideocheck=", StringComparison.OrdinalIgnoreCase));
        if (desktopVideoCheck is not null) { _ = Diagnostics.DesktopVideoRegression.Run(desktopVideoCheck["--desktopvideocheck=".Length..]); return; }
        var settingsFeatureCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--settingsfeaturescheck=", StringComparison.OrdinalIgnoreCase));
        if (settingsFeatureCheck is not null) { _ = Diagnostics.SettingsFeatureRegression.Run(settingsFeatureCheck["--settingsfeaturescheck=".Length..]); return; }
        var memoryCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--memorycheck=", StringComparison.OrdinalIgnoreCase));
        if (memoryCheck is not null) { _ = Diagnostics.MemoryRegression.Run(memoryCheck[14..]); return; }
        var shadowCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--shadowcheck=", StringComparison.OrdinalIgnoreCase));
        if (shadowCheck is not null) { _ = Diagnostics.ShadowRegression.Run(shadowCheck[14..]); return; }
        var popupPerformanceCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--popupperfcheck=", StringComparison.OrdinalIgnoreCase));
        var optionPanelsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--optionpanelscheck=", StringComparison.OrdinalIgnoreCase));
        if (optionPanelsCheck is not null) { _ = Diagnostics.OptionPanelsRegression.Run(optionPanelsCheck["--optionpanelscheck=".Length..]); return; }
        if (popupPerformanceCheck is not null) { _ = Diagnostics.PopupPerformanceRegression.Run(popupPerformanceCheck["--popupperfcheck=".Length..]); return; }
        var popupCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--popupcheck=", StringComparison.OrdinalIgnoreCase));
        if (popupCheck is not null) { _ = Diagnostics.PopupRegression.Run(popupCheck[13..]); return; }
        var mirrorCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--updatemirrorcheck=", StringComparison.OrdinalIgnoreCase));
        if (mirrorCheck is not null) { _ = Diagnostics.UpdateRegression.Run(mirrorCheck[20..], mirrorOnly: true); return; }
        var updateCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--updatecheck=", StringComparison.OrdinalIgnoreCase));
        if (updateCheck is not null) { _ = Diagnostics.UpdateRegression.Run(updateCheck[14..]); return; }
        var imageImportCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--imageimportcheck=", StringComparison.OrdinalIgnoreCase));
        if (imageImportCheck is not null) { _ = Diagnostics.ImageImportRegression.Run(imageImportCheck[19..]); return; }
        var displayCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--displaycheck=", StringComparison.OrdinalIgnoreCase));
        if (displayCheck is not null) { _ = Diagnostics.DisplayRegression.Run(displayCheck[15..]); return; }
        var boardStorageCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--boardstoragecheck=", StringComparison.OrdinalIgnoreCase));
        if (boardStorageCheck is not null) { _ = Diagnostics.BoardStorageRegression.Run(boardStorageCheck.Substring("--boardstoragecheck=".Length)); return; }
        var boardPageCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--boardpagecheck=", StringComparison.OrdinalIgnoreCase));
        var boardPerformanceCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--boardperformancecheck=", StringComparison.OrdinalIgnoreCase));
        if (boardPerformanceCheck is not null) { _ = Diagnostics.BoardPerformanceRegression.Run(boardPerformanceCheck["--boardperformancecheck=".Length..]); return; }
        if (boardPageCheck is not null) { _ = Diagnostics.BoardPageRegression.Run(boardPageCheck["--boardpagecheck=".Length..]); return; }
        var pageManagementCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pagemanagementcheck=", StringComparison.OrdinalIgnoreCase));
        if (pageManagementCheck is not null) { _ = Diagnostics.BoardManagementRegression.Run(pageManagementCheck["--pagemanagementcheck=".Length..]); return; }
        var editableShapeCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--editableshapecheck=", StringComparison.OrdinalIgnoreCase));
        if (editableShapeCheck is not null) { _ = Diagnostics.EditableShapeRegression.Run(editableShapeCheck["--editableshapecheck=".Length..]); return; }
        var shortcutCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--shortcutcheck=", StringComparison.OrdinalIgnoreCase));
        if (shortcutCheck is not null) { _ = Diagnostics.ShortcutRegression.Run(shortcutCheck["--shortcutcheck=".Length..]); return; }
        var geometryCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--geometrycheck=", StringComparison.OrdinalIgnoreCase));
        if (geometryCheck is not null) { _ = Diagnostics.GeometryRegression.Run(geometryCheck[16..]); return; }
        var pdfLifetimeCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pdflifetimecheck=", StringComparison.OrdinalIgnoreCase));
        if (pdfLifetimeCheck is not null) { _ = Diagnostics.PdfRegression.Run(pdfLifetimeCheck["--pdflifetimecheck=".Length..], lifetimeOnly: true); return; }
        var pdfCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--pdfcheck=", StringComparison.OrdinalIgnoreCase));
        if (pdfCheck is not null) { _ = Diagnostics.PdfRegression.Run(pdfCheck[11..]); return; }
        var extensionsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--extensionscheck=", StringComparison.OrdinalIgnoreCase));
        if (extensionsCheck is not null)
        {
            _ = Diagnostics.ExtensionsRegression.Run(extensionsCheck.Substring("--extensionscheck=".Length));
            return;
        }
        var screenFollowCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--screenfollowcheck=", StringComparison.OrdinalIgnoreCase));
        if (screenFollowCheck is not null)
        {
            _ = Diagnostics.ScreenTrackingRegression.Run(screenFollowCheck.Substring("--screenfollowcheck=".Length));
            return;
        }
        var classroomDesignCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--classroomdesigncheck=", StringComparison.OrdinalIgnoreCase));
        if (classroomDesignCheck is not null)
        {
            _ = Diagnostics.ClassroomWindowRegression.Run(classroomDesignCheck.Substring("--classroomdesigncheck=".Length), exitApplication: true);
            return;
        }
        var mediaFormatsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--mediaformatscheck=", StringComparison.OrdinalIgnoreCase));
        if (mediaFormatsCheck is not null)
        {
            _ = Diagnostics.MediaFormatsRegression.Run(mediaFormatsCheck.Substring("--mediaformatscheck=".Length));
            return;
        }
        var layerOrderCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--layerscheck=", StringComparison.OrdinalIgnoreCase));
        if (layerOrderCheck is not null)
        {
            _ = Diagnostics.LayerOrderRegression.Run(layerOrderCheck.Substring("--layerscheck=".Length));
            return;
        }
        var imageObjectsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--imageobjectscheck=", StringComparison.OrdinalIgnoreCase));
        if (imageObjectsCheck is not null)
        {
            _ = Diagnostics.ImageObjectsRegression.Run(imageObjectsCheck.Substring("--imageobjectscheck=".Length));
            return;
        }
        var shapeToolsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--shapetoolscheck=", StringComparison.OrdinalIgnoreCase));
        if (shapeToolsCheck is not null)
        {
            _ = Diagnostics.ShapeToolsRegression.Run(shapeToolsCheck.Substring("--shapetoolscheck=".Length));
            return;
        }
        var boardPopupCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--boardpopupcheck=", StringComparison.OrdinalIgnoreCase));
        if (boardPopupCheck is not null)
        {
            _ = Diagnostics.BoardPopupRegression.Run(boardPopupCheck.Substring("--boardpopupcheck=".Length));
            return;
        }
        var workspaceCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--workspacecheck=", StringComparison.OrdinalIgnoreCase));
        if (workspaceCheck is not null)
        {
            _ = Diagnostics.WorkspaceRegression.Run(workspaceCheck.Substring("--workspacecheck=".Length));
            return;
        }
        var eraserPreviewCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--eraserpreviewcheck=", StringComparison.OrdinalIgnoreCase));
        if (eraserPreviewCheck is not null)
        {
            _ = Diagnostics.EraserPreviewRegression.Run(eraserPreviewCheck.Substring("--eraserpreviewcheck=".Length));
            return;
        }
        var penOptionsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--penoptionscheck=", StringComparison.OrdinalIgnoreCase));
        if (penOptionsCheck is not null)
        {
            _ = Diagnostics.PenOptionsRegression.Run(penOptionsCheck.Substring("--penoptionscheck=".Length));
            return;
        }
        var settingsCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--settingscheck=", StringComparison.OrdinalIgnoreCase));
        if (settingsCheck is not null)
        {
            _ = Diagnostics.SettingsRegression.Run(settingsCheck.Substring("--settingscheck=".Length));
            return;
        }
        var activationHost = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--activationhost=", StringComparison.OrdinalIgnoreCase));
        if (activationHost is not null)
        {
            _activationHost = new Diagnostics.ActivationHost(activationHost.Substring("--activationhost=".Length));
            return;
        }
        var services = new AppServices();
        _shell = new ShellController(services);
        _shell.Start();
        var boardArgument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith("--") && Path.GetExtension(a).Equals(".icink", StringComparison.OrdinalIgnoreCase));
        if (boardArgument is not null) _ = _shell.OpenLibraryDocumentAsync(boardArgument);
        var pdfArgument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => a.StartsWith("--pdf=", StringComparison.OrdinalIgnoreCase)
            || !a.StartsWith("--") && Path.GetExtension(a).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdfArgument is not null)
        {
            string pdfPath = pdfArgument.StartsWith("--pdf=", StringComparison.OrdinalIgnoreCase) ? pdfArgument[6..] : pdfArgument;
            _ = _shell.OpenPdfAsync(pdfPath);
            var activationReport = Environment.GetCommandLineArgs().FirstOrDefault(argument => argument.StartsWith("--pdfactivationreport=", StringComparison.Ordinal));
            if (activationReport is not null) _ = Diagnostics.PdfActivationRegression.ObserveAsync(_shell, pdfPath, activationReport["--pdfactivationreport=".Length..]);
        }

        // "--board" opens straight into the blackboard, mirroring the original launcher switches.
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (string.Equals(a, "--board", StringComparison.OrdinalIgnoreCase))
                _shell.ToggleBoard();

            // Start with clicks falling through to the desktop, so launching the app does not
            // trap the mouse on a machine that is in use. Toggle it off from the bar to draw.
            if (string.Equals(a, "--passthrough", StringComparison.OrdinalIgnoreCase))
                _shell.StartInClickThrough();
        }

        var diag = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--diag=", StringComparison.OrdinalIgnoreCase));
        if (diag is not null)
        {
            Ink.Rendering.OverlayInkPresenter.LatencyLogEnabled = true;
            string path = diag.Substring("--diag=".Length);
            var t = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(700);
            t.Tick += (_, _) =>
            {
                try
                {
                    var w = _shell.Overlay.Window;
                    File.WriteAllText(path,
                        $"pointerMessages={w.PointerMessageCount} (down={w.PointerDownCount} move={w.PointerMoveCount} up={w.PointerUpCount}); "
                      + $"mouseMessages={w.MouseMessageCount}; "
                      + $"mouseAsPointer={Platform.Windows.LayeredInkWindow.MouseAsPointer}; "
                      + $"clickThrough={w.ClickThrough}; visible={w.IsVisible}; "
                      + $"strokes={_shell.Services.OverlayInk.Document.Count}; "
                      + $"live={_shell.Services.OverlayInk.LiveStrokes.Count}; "
                      + $"tool={_shell.Services.OverlayInk.Tool}\n");
                }
                catch { }
            };
            t.Start();
        }

        var open = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--open=", StringComparison.OrdinalIgnoreCase));
        if (open is not null)
        {
            var kinds = new Queue<string>(open.Substring("--open=".Length).Split(','));
            var t0 = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            t0.Interval = TimeSpan.FromMilliseconds(1100);
            t0.Tick += (_, _) =>
            {
                if (kinds.Count == 0) { t0.Stop(); return; }
                var k = kinds.Dequeue();
                {
                    switch (k.Trim())
                    {
                        case "settings": _shell.OpenSettings(); break;
                        case "countdown": _shell.OpenTimer(); break;
                        case "lottery": _shell.OpenLottery(); break;
                        case "guide": _shell.OpenGuide(); break;
                        case "replay": _shell.OpenReplay(); break;
                        case "pen": _shell.OpenPenPalette(false); break;
                        case "highlighter": _shell.OpenPenPalette(true); break;
                        case "shapes": _shell.OpenShapePicker(); break;
                        case "board": _shell.ToggleBoard(); break;
                    }
                }
            };
            t0.Start();
        }

        var shot = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase));
        if (shot is not null)
        {
            string dir = shot.Substring("--shot=".Length);
            Directory.CreateDirectory(dir);
            // Render each window shortly after it activates: RenderTargetBitmap on a
            // non-foreground WinUI window can hang, so every window captures itself.
            void Hook(Microsoft.UI.Xaml.Window w)
            {
                w.Activated += async (_, _) =>
                {
                    await System.Threading.Tasks.Task.Delay(700);
                    try
                    {
                        if (w.Content is not FrameworkElement fe || fe.XamlRoot is null) return;
                        var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                        await rtb.RenderAsync(fe);
                        var pixels = await rtb.GetPixelsAsync();
                        var byteArr = new byte[pixels.Length];
                        Windows.Storage.Streams.DataReader.FromBuffer(pixels).ReadBytes(byteArr);
                        var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ras);
                        enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, byteArr);
                        await enc.FlushAsync();
                        ras.Seek(0);
                        string name = string.Join("_", (w.Title ?? "window").Split(Path.GetInvalidFileNameChars()));
                        using var fs = File.Create(Path.Combine(dir, name + ".png"));
                        await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(ras).CopyToAsync(fs);
                    }
                    catch { }
                };
            }
            foreach (var w in _shell.OpenWindows) Hook(w);
            _shell.WindowOpened += w => Hook(w);
        }



        var activationCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--activationcheck=", StringComparison.OrdinalIgnoreCase));
        if (activationCheck is not null)
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1); timer.IsRepeating = false;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await Diagnostics.ActivationRegression.Run(_shell, activationCheck.Substring("--activationcheck=".Length));
            };
            timer.Start();
        }

        var classroomCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--classroomcheck=", StringComparison.OrdinalIgnoreCase));
        if (classroomCheck is not null)
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1); timer.IsRepeating = false;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await Diagnostics.ClassroomWindowRegression.Run(classroomCheck.Substring("--classroomcheck=".Length));
            };
            timer.Start();
        }

        var interactionCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--interactioncheck=", StringComparison.OrdinalIgnoreCase));
        if (interactionCheck is not null)
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1); timer.IsRepeating = false;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await Diagnostics.InteractionRegression.Run(interactionCheck.Substring("--interactioncheck=".Length));
            };
            timer.Start();
        }

        var writingCheck = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--writingcheck=", StringComparison.OrdinalIgnoreCase));
        if (writingCheck is not null)
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1); timer.IsRepeating = false;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try { await Diagnostics.WritingPerformance.Run(writingCheck.Substring("--writingcheck=".Length)); }
                finally { _shell.Dispose(); Application.Current.Exit(); }
            };
            timer.Start();
        }

        var performanceCheck = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--perfcheck=", StringComparison.OrdinalIgnoreCase));
        if (performanceCheck is not null)
        {
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.IsRepeating = false;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await Diagnostics.PerformanceCheck.Run(_shell, performanceCheck.Substring("--perfcheck=".Length));
            };
            timer.Start();
        }

        var selfTest = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase));
        if (selfTest is not null)
        {
            string dir = selfTest.Substring("--selftest=".Length);
            // Let the first frame present before drawing, so the report counts real frames.
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1200);
            timer.IsRepeating = false;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await Diagnostics.SelfTest.Run(_shell, dir);
            };
            timer.Start();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // A classroom tool must not vanish mid-lesson. Log and keep running where possible.
        Core.Diagnostics.FileLog.WriteLogToFile(e.Exception.ToString(), Core.Diagnostics.LogType.Error);
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"{DateTime.Now:O} {e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;
    }
}
