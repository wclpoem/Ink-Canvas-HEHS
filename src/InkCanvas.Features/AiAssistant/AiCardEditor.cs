using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;

namespace InkCanvas.Features.AiAssistant;

/// <summary>
/// 主展板与板中板共用的 AI 卡片编辑器：按 Kind 编辑标题/作者/正文/LaTeX，
/// 保存时重渲染（公式优先 LaTeX 位图，失败回退文本）并一次 <see cref="BoardService.UpdateImage"/>。
/// 渲染/写入失败只提示，不改动原对象。
/// </summary>
public static class AiCardEditor
{
    public const int FormulaMaxWidthDip = 560;

    public static async Task<bool> EditAsync(
        BoardService board, BoardImageAsset asset, AiPasteMetadata metadata, XamlRoot xamlRoot)
    {
        if (board is null || asset is null || metadata is null || xamlRoot is null) return false;

        AiCardEditState state;
        try
        {
            state = AiCardEditState.FromMetadata(metadata);
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] AI 卡片编辑失败：{asset.Name}，读取元数据异常 {ex.Message}", LogType.Error);
            return false;
        }

        FileLog.WriteLogToFile(
            $"[AI] AI 卡片编辑：资产 {asset.Name}，Kind={metadata.Kind}，BlockKind={state.BlockKind}",
            LogType.Event);

        var panel = new StackPanel { Spacing = 6 };
        TextBox titleEditor = null;
        TextBox authorEditor = null;

        if (state.HasTitleEditor)
        {
            titleEditor = new TextBox
            {
                Text = state.Title,
                PlaceholderText = "标题",
                MinWidth = 360,
            };
            panel.Children.Add(EditorLabel(state.TitleLabel));
            panel.Children.Add(titleEditor);
        }

        if (state.HasAuthorEditor)
        {
            authorEditor = new TextBox
            {
                Text = state.Author,
                PlaceholderText = "作者 / 朝代",
                MinWidth = 360,
            };
            panel.Children.Add(EditorLabel(state.AuthorLabel));
            panel.Children.Add(authorEditor);
        }

        var editor = new TextBox
        {
            Text = state.Body,
            AcceptsReturn = state.BodyAcceptsReturn,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = state.EditorMinWidth,
            MinHeight = state.EditorMinHeight,
        };
        panel.Children.Add(EditorLabel(state.EditorLabel));
        panel.Children.Add(editor);

        var dialog = new ContentDialog
        {
            Title = state.DialogTitle,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] AI 卡片编辑失败：{asset.Name}，对话框异常 {ex.Message}", LogType.Error);
            return false;
        }

        string body = editor.Text ?? "";
        if (state.IsFormula && string.IsNullOrWhiteSpace(body))
        {
            await ShowMessageAsync(xamlRoot, "LaTeX 内容不能为空。");
            return false;
        }

        AiPasteMetadata updated = state.ApplyTo(
            metadata,
            titleEditor?.Text ?? state.Title,
            authorEditor?.Text ?? state.Author,
            body);

        try
        {
            bool success = state.IsBoardPlan
                ? await RenderBoardPlanAsync(board, asset, updated, state, body, xamlRoot)
                : await RenderLegacyAsync(board, asset, updated, xamlRoot);

            if (success)
            {
                FileLog.WriteLogToFile(
                    $"[AI] AI 卡片编辑完成：{asset.Name}，kind={metadata.Kind}，"
                    + $"blockKind={updated.BlockKind}，body={body.Length} 字",
                    LogType.Event);
            }

            return success;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile(
                $"[AI] AI 卡片编辑失败：{asset.Name}，{ex.Message ?? ex.GetType().Name}", LogType.Error);
            await ShowMessageAsync(xamlRoot, "卡片重新渲染失败，原内容保持不变。");
            return false;
        }
    }

    /// <summary>
    /// 编辑原生文本对象（正文/标题/作者/字体/字号/颜色/对齐/行距/列数）。
    /// 保存只调用一次 <see cref="BoardService.UpdateAiObject"/>，不生成任何 PNG。
    /// </summary>
    public static async Task<bool> EditTextAsync(
        BoardService board, BoardTextObject text, XamlRoot xamlRoot)
    {
        if (board is null || text is null || xamlRoot is null) return false;

        var panel = new StackPanel { Spacing = 6 };
        TextBox title = Field(panel, "标题", text.Title, "标题");
        TextBox author = Field(panel, "作者 / 朝代", text.Author, "作者 / 朝代");
        TextBox content = Field(panel, "正文（词语列表每行一个）", text.Content, "正文",
            multiLine: true, minHeight: 140);
        TextBox fontFamily = Field(panel, "字体", text.FontFamily, "Microsoft YaHei UI");
        TextBox fontSize = Field(panel, "字号", text.FontSize.ToString("0.##", CultureInfo.InvariantCulture), "32");
        TextBox color = Field(panel, "颜色 (#RRGGBB / #AARRGGBB)", text.Color, "#FF202024");
        TextBox lineHeight = Field(panel, "行距倍数", text.LineHeight.ToString("0.##", CultureInfo.InvariantCulture), "1.4");

        var align = new ComboBox { MinWidth = 220, SelectedIndex = AlignIndex(text.Align) };
        foreach (string item in new[] { "左对齐", "居中", "右对齐", "两端对齐" }) align.Items.Add(item);
        panel.Children.Add(EditorLabel("对齐"));
        panel.Children.Add(align);

        var columns = new ComboBox { MinWidth = 220, SelectedIndex = Math.Clamp(text.Columns, 1, 3) - 1 };
        foreach (string item in new[] { "1 列", "2 列", "3 列" }) columns.Items.Add(item);
        panel.Children.Add(EditorLabel("列数（词语列表）"));
        panel.Children.Add(columns);

        var dialog = new ContentDialog
        {
            Title = "编辑 AI 文本",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] 原生文本编辑失败：对话框异常 {ex.Message}", LogType.Error);
            return false;
        }

        string body = content.Text ?? "";
        string heading = title.Text ?? "";
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(heading))
        {
            await ShowMessageAsync(xamlRoot, "正文和标题不能同时为空。");
            return false;
        }

        double fontSizeValue = ParseNumber(fontSize.Text, text.FontSize);
        double lineHeightValue = ParseNumber(lineHeight.Text, text.LineHeight);
        int columnValue = Math.Clamp(columns.SelectedIndex + 1, 1, 3);
        string alignValue = AlignValue(align.SelectedIndex);

        bool updated = board.UpdateAiObject(text, target =>
        {
            target.Title = heading;
            target.Author = author.Text ?? "";
            target.Content = body;
            target.FontFamily = string.IsNullOrWhiteSpace(fontFamily.Text)
                ? "Microsoft YaHei UI" : fontFamily.Text.Trim();
            target.FontSize = fontSizeValue;
            target.Color = string.IsNullOrWhiteSpace(color.Text) ? "#FF202024" : color.Text.Trim();
            target.LineHeight = lineHeightValue;
            target.Align = alignValue;
            target.Columns = columnValue;
            AiCardEditState.MarkTeacherEdited(target);
        }, "编辑 AI 文本");

        if (!updated)
            await ShowMessageAsync(xamlRoot, "内容未变化，或对象已不在当前页面。");
        else
            FileLog.WriteLogToFile(
                $"[AI] 原生文本编辑完成：id={text.Id}，columns={columnValue}，chars={body.Length}",
                LogType.Event);
        return updated;
    }

    /// <summary>编辑原生公式对象；LaTeX 是唯一源，保存后由矢量公式渲染器重排。</summary>
    public static async Task<bool> EditFormulaAsync(
        BoardService board, BoardFormulaObject formula, XamlRoot xamlRoot)
    {
        if (board is null || formula is null || xamlRoot is null) return false;

        var panel = new StackPanel { Spacing = 6 };
        TextBox latex = Field(panel, "LaTeX（保存后矢量重排，不生成图片）", formula.Latex, "\\frac{1}{2}x^2",
            multiLine: true, minHeight: 120);
        TextBox fontSize = Field(panel, "字号", formula.FontSize.ToString("0.##", CultureInfo.InvariantCulture), "30");
        TextBox color = Field(panel, "颜色 (#RRGGBB / #AARRGGBB)", formula.Color, "#FF202024");

        var dialog = new ContentDialog
        {
            Title = "编辑 AI 公式",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] 原生公式编辑失败：对话框异常 {ex.Message}", LogType.Error);
            return false;
        }

        string source = latex.Text ?? "";
        if (string.IsNullOrWhiteSpace(source))
        {
            await ShowMessageAsync(xamlRoot, "LaTeX 内容不能为空。");
            return false;
        }

        double fontSizeValue = ParseNumber(fontSize.Text, formula.FontSize);
        bool updated = board.UpdateAiObject(formula, target =>
        {
            target.Latex = source.Trim();
            target.FontSize = fontSizeValue;
            target.Color = string.IsNullOrWhiteSpace(color.Text) ? "#FF202024" : color.Text.Trim();
            AiCardEditState.MarkTeacherEdited(target);
        }, "编辑 AI 公式");

        if (!updated)
            await ShowMessageAsync(xamlRoot, "公式未变化，或对象已不在当前页面。");
        else
            FileLog.WriteLogToFile(
                $"[AI] 原生公式编辑完成：id={formula.Id}，latex={source.Length} 字符",
                LogType.Event);
        return updated;
    }

    private static TextBox Field(
        StackPanel panel, string label, string value, string placeholder,
        bool multiLine = false, double minHeight = 0)
    {
        var box = new TextBox
        {
            Text = value ?? "",
            PlaceholderText = placeholder ?? "",
            MinWidth = 440,
            AcceptsReturn = multiLine,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (minHeight > 0) box.MinHeight = minHeight;
        panel.Children.Add(EditorLabel(label));
        panel.Children.Add(box);
        return box;
    }

    private static int AlignIndex(string align) => (align ?? "").Trim().ToLowerInvariant() switch
    {
        "center" => 1,
        "right" => 2,
        "justify" => 3,
        _ => 0,
    };

    private static string AlignValue(int index) => index switch
    {
        1 => "center",
        2 => "right",
        3 => "justify",
        _ => "left",
    };

    private static double ParseNumber(string text, double fallback)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value))
            return value;
        return double.TryParse(text, out value) && double.IsFinite(value) ? value : fallback;
    }

    private static async Task<bool> RenderBoardPlanAsync(
        BoardService board,
        BoardImageAsset asset,
        AiPasteMetadata updated,
        AiCardEditState state,
        string body,
        XamlRoot xamlRoot)
    {
        byte[] png;
        double width;
        double height;
        string fallbackReason = "";

        if (state.IsFormula)
        {
            var host = xamlRoot.Content as FrameworkElement;
            BoardFormulaRenderResult formula = await BoardFormulaRenderer.RenderPngAsync(
                updated.Latex, FormulaMaxWidthDip, 2.0, host);

            if (formula.Success)
            {
                png = formula.Png;
                width = formula.Width;
                height = formula.Height;
            }
            else
            {
                fallbackReason = string.IsNullOrWhiteSpace(formula.Error)
                    ? "公式渲染不可用" : formula.Error;
                BoardTextCardRenderResult fallback = await BoardTextCardRenderer.RenderAsync(
                    state.BuildFormulaFallbackSpec(updated, updated.Latex));
                png = fallback.Png;
                width = fallback.Width;
                height = fallback.Height;
            }
        }
        else
        {
            BoardTextCardRenderResult rendered = await BoardTextCardRenderer.RenderAsync(
                state.BuildTextSpec(updated, updated.Title, updated.Author, body));
            png = rendered.Png;
            width = rendered.Width;
            height = rendered.Height;
        }

        if (png is not { Length: > 0 } || width <= 0 || height <= 0)
        {
            await ShowMessageAsync(xamlRoot, "卡片重新渲染失败，原内容保持不变。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(fallbackReason))
        {
            string note = "手动编辑时 LaTeX 排版回退为文本：" + fallbackReason;
            if (!updated.Notes.Contains(note)) updated.Notes.Add(note);
            FileLog.WriteLogToFile(
                $"[AI] 板书卡片编辑回退：{asset.Name}，{fallbackReason}", LogType.Error);
        }

        FileLog.WriteLogToFile(
            $"[AI] 板书卡片重渲染：{asset.Name}，blockKind={updated.BlockKind}，"
            + $"formula={state.IsFormula}，size={width:0}×{height:0}，lines={updated.Lines.Count}",
            LogType.Event);

        if (!board.UpdateImage(asset, png, updated.Serialize()))
        {
            await ShowMessageAsync(xamlRoot, "卡片已不在当前页面，未更新。");
            return false;
        }

        return true;
    }

    private static async Task<bool> RenderLegacyAsync(
        BoardService board, BoardImageAsset asset, AiPasteMetadata updated, XamlRoot xamlRoot)
    {
        List<string> lines = AiCardEditState.ContentLines(updated);
        var (estimatedWidth, estimatedHeight) = PasteCardLayout.Estimate(lines);
        int width = Math.Clamp((int)Math.Round(Math.Max(asset.Width, estimatedWidth)), 120, 640);
        int height = Math.Clamp((int)Math.Round(Math.Max(asset.Height, estimatedHeight)), 60, 480);

        byte[] png = await PasteCardRenderer.RenderPngAsync(asset.Name, lines, width, height);
        if (png is null || png.Length == 0)
        {
            await ShowMessageAsync(xamlRoot, "卡片重新渲染失败，原内容保持不变。");
            return false;
        }

        if (!board.UpdateImage(asset, png, updated.Serialize()))
        {
            await ShowMessageAsync(xamlRoot, "卡片已不在当前页面，未更新。");
            return false;
        }

        return true;
    }

    private static TextBlock EditorLabel(string text) => new() { Text = text, Opacity = 0.7 };

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
                XamlRoot = xamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] AI 卡片提示失败：{message}，{ex.Message}", LogType.Trace);
        }
    }
}