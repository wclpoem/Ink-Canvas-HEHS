using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace InkCanvas.Modules.AiAssistant;

public sealed partial class AssistantWindow
{
    private async Task PickAttachmentsAsync()
    {
        var chat = _chat;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            foreach (var file in files)
            {
                var attachment = await AssistantAttachments.FromPathAsync(file.Path);
                if (_closed || !ReferenceEquals(chat, _chat)) return;
                AddAttachment(attachment);
            }
        }
        catch (Exception exception) { if (!_closed) ReportError(exception.Message); }
    }

    private void RenderAttachments()
    {
        _attachments.Children.Clear();
        if (_chat is null) return;
        foreach (var attachment in _chat.DraftAttachments)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = new TextBlock { Text = (attachment.IsImage ? "图片 · " : "文件 · ") + attachment.Name,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            ToolTipService.SetToolTip(label, attachment.IsImage ? attachment.Name : attachment.Path); row.Children.Add(label);
            var remove = IconButton("移除附件 " + attachment.Name, Symbol.Cancel);
            remove.Click += (_, _) => { _chat.DraftAttachments.Remove(attachment); RenderAttachments(); Changed?.Invoke(); };
            Grid.SetColumn(remove, 1); row.Children.Add(remove); _attachments.Children.Add(row);
        }
    }
}
