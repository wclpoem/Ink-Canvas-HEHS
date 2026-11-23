using InkCanvas.App.Services;
using InkCanvas.Features.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace InkCanvas.App.Views;

/// <summary>Shared roster editor for settings and the standalone classroom window.</summary>
internal sealed class ClassroomRosterEditor : Grid
{
    private readonly Window _owner;
    private readonly string _directory, _path;
    private readonly TextBox _box;
    private readonly TextBlock _status;
    private readonly List<Button> _actions = new();
    private string _origin = "";
    private bool _loaded, _closed, _busy;
    internal string RosterText { get => _box.Text; set => _box.Text = value; }
    internal bool IsBusy => _busy;
    internal bool HasChanges => _box.Text != _origin;
    internal event Action<IReadOnlyList<string>> Saved;

    internal ClassroomRosterEditor(Window owner, string directory)
    {
        _owner = owner; _directory = directory; _path = Path.Combine(directory, "Names.txt");
        RowSpacing = 16;
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(new TextBlock { Text = "每行一人，可粘贴表格或导入 TXT、CSV、TSV 文件。表格优先读取姓名列。",
            FontSize = 14, TextWrapping = TextWrapping.Wrap });
        _box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontSize = 20,
            MinHeight = 220, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetName(_box, "课堂名单");
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        Grid.SetRow(_box, 1); Children.Add(_box);
        _status = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        _box.TextChanged += (_, _) => UpdateStatus();
        Grid.SetRow(_status, 2); Children.Add(_status);
        var actions = new Grid { ColumnSpacing = 8 };
        Add("导入文件", "\uF433", () => _ = ImportAsync());
        Add("粘贴名单", "\uF2D6", () => _ = PasteAsync());
        Add("保存名单", "\uF68A", () => _ = SaveAsync(), true);
        Grid.SetRow(actions, 3); Children.Add(actions);
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            string initial = _box.Text;
            try
            {
                if (File.Exists(_path))
                {
                    string text = await File.ReadAllTextAsync(_path);
                    text = string.Join(Environment.NewLine, text.ReplaceLineEndings("\n").Split('\n').Select(name =>
                        name.Contains(',') || name.Contains('"') ? "\"" + name.Replace("\"", "\"\"") + "\"" : name));
                    if (!_closed && _box.Text == initial && initial.Length == 0) { _box.Text = text; _origin = text; }
                }
                if (!_closed) UpdateStatus();
            }
            catch { if (!_closed) _status.Text = "无法读取原名单，可输入后重新保存。"; }
        };

        void Add(string label, string glyph, Action action, bool primary = false)
        {
            int index = actions.ColumnDefinitions.Count;
            actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var button = ClassroomControls.Button(label, action, primary, glyph);
            button.FontSize = 16; button.Padding = new Thickness(6);
            Grid.SetColumn(button, index); actions.Children.Add(button); _actions.Add(button);
        }
    }

    private void UpdateStatus() => _status.Text = $"共 {ClassroomRoster.Parse(_box.Text).Count} 人" + (HasChanges ? " · 有未保存的更改" : "");

    private void SetBusy(bool value)
    {
        _busy = value;
        _box.IsReadOnly = value;
        foreach (var action in _actions) action.IsEnabled = !value;
    }

    private async Task ImportAsync()
    {
        if (_busy || _closed) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (string extension in new[] { ".txt", ".csv", ".tsv" }) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowChrome.GetHwnd(_owner));
            var file = await picker.PickSingleFileAsync();
            if (file is null || _closed) return;
            string text = ClassroomRoster.Decode(await File.ReadAllBytesAsync(file.Path));
            if (!_closed) _box.Text = string.Join(Environment.NewLine, ClassroomRoster.Parse(text));
        }
        catch (Exception ex) { if (!_closed) _status.Text = $"导入失败，请检查文件。{ex.Message}"; }
        finally { if (!_closed) SetBusy(false); }
    }

    private async Task PasteAsync()
    {
        if (_busy || _closed) return;
        SetBusy(true);
        try
        {
            var data = Clipboard.GetContent();
            if (!data.Contains(StandardDataFormats.Text)) return;
            string text = await data.GetTextAsync();
            if (!_closed) _box.Text = string.Join(Environment.NewLine, ClassroomRoster.Parse(text));
        }
        catch { if (!_closed) _status.Text = "暂时无法读取剪贴板，也可直接在输入框中粘贴。"; }
        finally { if (!_closed) SetBusy(false); }
    }

    internal async Task<bool> SaveAsync()
    {
        if (_busy || _closed) return false;
        SetBusy(true);
        string original = _box.Text;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string normalized = string.Join(Environment.NewLine, ClassroomRoster.Parse(original));
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(temporary, normalized);
            File.Move(temporary, _path, overwrite: true);
            _origin = original;
            if (!_closed) { UpdateStatus(); _status.Text += " · 已保存"; Saved?.Invoke(ClassroomRoster.Load(_directory)); }
            return true;
        }
        catch (Exception ex) { if (!_closed) _status.Text = $"保存失败，名单仍保留在输入框中。{ex.Message}"; return false; }
        finally
        {
            if (!_closed) SetBusy(false);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal async Task<bool> ConfirmCloseAsync()
    {
        if (_busy) return false;
        if (!HasChanges) return true;
        var dialog = new ContentDialog { Title = "保存名单更改", Content = "是否保存当前名单？",
            PrimaryButtonText = "保存", SecondaryButtonText = "放弃更改", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Secondary || result == ContentDialogResult.Primary && await SaveAsync();
    }

    internal void Close() => _closed = true;
}
