using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly Border _saveBadge = new() { Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(5), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _saveState = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
    private readonly TextBlock _documentName = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _statusProgress = new() { Width = 14, Height = 14, MinWidth = 0, MinHeight = 0, Visibility = Visibility.Collapsed };
    private readonly InfoBar _statusNotice = new() { IsOpen = false, IsClosable = true, MaxWidth = 560,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
    private string _statusMessage;
    private bool _searching;
    internal string StatusText => _status.Text;

    private void ConfigureDocumentStatus(WindowFrame frame)
    {
        frame.Header.Resources["TitleBarContentHorizontalAlignment"] = HorizontalAlignment.Left;
        var badge = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        badge.Children.Add(_statusProgress); badge.Children.Add(_saveState);
        _saveBadge.Child = badge;
        var title = new Grid { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        title.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        title.Children.Add(_documentName);
        Grid.SetColumn(_saveBadge, 1); title.Children.Add(_saveBadge);
        frame.Header.Content = title;
        _statusNotice.Content = _status;
        _statusNotice.Closed += (_, _) => { _statusMessage = null; _status.Text = ""; };
        Grid.SetRow(_statusNotice, 1); _root.Children.Add(_statusNotice);
        AutomationProperties.SetName(_status, "PDF 状态信息");
        AutomationProperties.SetName(_saveState, "PDF 保存状态");
        AutomationProperties.SetName(_documentName, "PDF 文档名称");
        AutomationProperties.SetName(_statusNotice, "PDF 文档通知");
    }

    private void UpdateDocumentTitle()
    {
        Title = Path.GetFileName(_targetPath) + " — Ink Canvas PDF";
        _documentName.Text = Path.GetFileName(_targetPath);
        ToolTipService.SetToolTip(_documentName, _targetPath);
        if (Content is WindowFrame frame) frame.Header.Title = "";
    }

    private void SetStatus(string text)
    {
        if (_closed) return;
        _statusMessage = text;
        UpdateDocumentStatus();
    }

    private void UpdateDocumentStatus()
    {
        if (_closed || Controller is null) return;
        _statusProgress.IsActive = _saving;
        _statusProgress.Visibility = _saving ? Visibility.Visible : Visibility.Collapsed;
        _saveState.Text = _saving ? "保存中" : _file.ReadOnly ? "只读" : IsDirty ? "未保存" : "已保存";
        _saveButton.IsEnabled = !_saving && !_file.ReadOnly;
        string hint = _file.ReadOnly ? "此文件只允许查看" : _file.HasSignatures ? "含数字签名，保存批注时将另存为副本" : "批注直接保存为 PDF 矢量墨迹";
        _status.Text = _statusMessage ?? hint;
        ToolTipService.SetToolTip(_saveBadge, _statusMessage ?? hint);
        _statusNotice.Severity = _statusMessage?.Contains("失败", StringComparison.Ordinal) == true ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        _statusNotice.IsOpen = _statusMessage is not null && !_statusMessage.StartsWith("正在保存", StringComparison.Ordinal)
            && !_statusMessage.StartsWith("已保存", StringComparison.Ordinal) && !_statusMessage.StartsWith("找到", StringComparison.Ordinal);
        UpdateReadingControls();
    }
}
