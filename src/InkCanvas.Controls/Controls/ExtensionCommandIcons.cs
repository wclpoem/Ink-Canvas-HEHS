using InkCanvas.Extensibility;

namespace InkCanvas.Controls.Controls;

/// <summary>Shared module icons for the screen and board toolbars.</summary>
public static class ExtensionCommandIcons
{
    public static string For(ExtensionCommand command) => (command.PluginId, command.CommandId) switch
    {
        ("inkcanvas.pdf", "open") => Glyphs.Pdf,
        ("inkcanvas.pdf", "import") => Glyphs.Import,
        ("inkcanvas.analytic-geometry", "open") => Glyphs.Calculator,
        ("inkcanvas.ai-assistant", "open") => Glyphs.Ai,
        ("inkcanvas.library", "open") => Glyphs.SlideList,
        _ => Glyphs.Tools,
    };
}
