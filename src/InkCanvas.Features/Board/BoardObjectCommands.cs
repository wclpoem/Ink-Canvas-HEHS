using System.Numerics;
using InkCanvas.Core.History;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board;

/// <summary>
/// 统一板书对象命令入口（阶段 2A）。全部为既有实现的薄封装：
/// Create → AddImagesAtomic/AddAiObjectsAtomic/AddMedia；Edit → UpdateAiObject/RecordImageChange；
/// Transform → InkController.TransformObjectsUndoable；Delete → 一次 DelegateAction 删除笔画与元素；
/// Group/Ungroup → 文本/公式/图片/媒体共用 string GroupId。
/// 旧 API（AddImage/RemoveAiObjects/RemoveImage/RecordImage* 等）保持不变。
/// </summary>
public sealed partial class BoardService
{
    /// <summary>
    /// 批量创建对象：按底层类型分流到既有原子写入命令。<paramref name="groupId"/> 为 null 时
    /// 由 AddAiObjectsAtomic 生成；图片/媒体沿用各自命令的撤销步。页令牌不匹配时返回空集合。
    /// </summary>
    public IReadOnlyList<Guid> CreateObjects(
        Guid pageId, IEnumerable<IBoardObject> objects, string actionName = null, string groupId = null)
    {
        if (pageId != Guid.Empty && pageId != CurrentPageId)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 拒绝写入对象：页令牌不匹配 expected={pageId} actual={CurrentPageId}", Core.Diagnostics.LogType.Info);
            return Array.Empty<Guid>();
        }
        if (objects is null) return Array.Empty<Guid>();

        var images = new List<BoardImageAsset>();
        var texts = new List<BoardTextObject>();
        var formulas = new List<BoardFormulaObject>();
        var media = new List<BoardMediaAsset>();
        foreach (IBoardObject item in objects)
        {
            switch (item?.Source)
            {
                case BoardImageAsset image: images.Add(image); break;
                case BoardMediaAsset asset: media.Add(asset); break;
                case BoardTextObject text: texts.Add(text); break;
                case BoardFormulaObject formula: formulas.Add(formula); break;
            }
        }

        var ids = new List<Guid>();
        if (images.Count > 0)
        {
            AddImagesAtomic(images, actionName);
            ids.AddRange(images.Select(image => image.Id));
        }
        if (texts.Count + formulas.Count > 0)
            ids.AddRange(AddAiObjectsAtomic(texts, formulas, actionName, groupId));
        foreach (BoardMediaAsset asset in media)
        {
            if (!AddMedia(asset)) continue;
            ids.Add(asset.Id);
        }
        return ids;
    }

    /// <summary>
    /// 按 id 编辑对象属性（文本/公式正文与样式、图片/媒体名称与几何）。before 快照在命令内部捕获，
    /// 每个对象记录一次撤销；图片字节更新仍走 <see cref="UpdateImage"/>。返回实际修改数量。
    /// </summary>
    public int EditObjects(
        Guid pageId, IEnumerable<Guid> ids, Action<object> edit, string actionName = null)
    {
        int index = ResolvePageIndex(pageId);
        if (index < 0 || edit is null) return 0;
        if (index != CurrentIndex)
        {
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 编辑命令仅支持当前页：pageId={pageId}", Core.Diagnostics.LogType.Info);
            return 0;
        }

        var idSet = ids?.Where(id => id != Guid.Empty).ToHashSet() ?? new HashSet<Guid>();
        if (idSet.Count == 0) return 0;

        int changed = 0;
        string name = string.IsNullOrWhiteSpace(actionName) ? "编辑对象" : actionName.Trim();
        foreach (BoardTextObject text in _textObjects[index].Where(item => idSet.Contains(item.Id)).ToArray())
        {
            if (UpdateAiObject(text, target => edit(target), name)) changed++;
        }
        foreach (BoardFormulaObject formula in _formulaObjects[index].Where(item => idSet.Contains(item.Id)).ToArray())
        {
            if (UpdateAiObject(formula, target => edit(target), name)) changed++;
        }
        foreach (BoardImageAsset image in _images[index].Where(item => idSet.Contains(item.Id)).ToArray())
        {
            var before = image.Copy();
            edit(image);
            int undoBefore = _pages[index].History.UndoCount;
            RecordImageChange(image, before, name);
            if (_pages[index].History.UndoCount > undoBefore) changed++;
        }
        foreach (BoardMediaAsset asset in _media[index].Where(item => idSet.Contains(item.Id)).ToArray())
        {
            var before = asset.Copy();
            edit(asset);
            int undoBefore = _pages[index].History.UndoCount;
            RecordMediaChange(asset, before, name);
            if (_pages[index].History.UndoCount > undoBefore) changed++;
        }
        return changed;
    }

    /// <summary>
    /// 对 id 命中的笔画/元素应用一次可撤销矩阵变换（分组对象由调用方传入组内全部 id）。
    /// 薄封装 <see cref="InkController.TransformObjectsUndoable"/>，不改变当前选择。
    /// </summary>
    public bool TransformObjects(
        Guid pageId, IEnumerable<Guid> ids, Matrix3x2 matrix, string actionName = null)
    {
        int index = ResolvePageIndex(pageId);
        if (index < 0) return false;
        var idSet = ids?.Where(id => id != Guid.Empty).ToHashSet() ?? new HashSet<Guid>();
        if (idSet.Count == 0) return false;

        var page = _pages[index];
        var strokes = page.Document.Strokes.Where(stroke => idSet.Contains(stroke.Id)).ToList();
        var elements = GetSelectionElements(page).Where(element => idSet.Contains(element.Id)).ToList();
        if (strokes.Count == 0 && elements.Count == 0) return false;

        page.TransformObjectsUndoable(strokes, elements, matrix,
            string.IsNullOrWhiteSpace(actionName) ? "变换对象" : actionName.Trim());
        ImagesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 统一删除：笔画、图片、媒体、文本、公式一次性移除并作为一次撤销步骤；
    /// 删除/撤销分别触发 <see cref="ImagesChanged"/> 与 <see cref="AiObjectsChanged"/>
    /// （AiStep remove/add），保证时间轴与画板一致。返回删除的对象数量。
    /// </summary>
    public int DeleteObjects(Guid pageId, IEnumerable<Guid> ids, string actionName = null)
    {
        int index = ResolvePageIndex(pageId);
        if (index < 0) return 0;
        var idSet = ids?.Where(id => id != Guid.Empty).ToHashSet() ?? new HashSet<Guid>();
        if (idSet.Count == 0) return 0;

        var page = _pages[index];
        var document = page.Document;
        var indexedStrokes = document.Strokes
            .Select((stroke, at) => (Index: at, Stroke: stroke))
            .Where(entry => idSet.Contains(entry.Stroke.Id)).ToArray();
        var indexedImages = _images[index]
            .Select((image, at) => (Index: at, Image: image))
            .Where(entry => idSet.Contains(entry.Image.Id)).ToArray();
        var indexedMedia = _media[index]
            .Select((asset, at) => (Index: at, Media: asset))
            .Where(entry => idSet.Contains(entry.Media.Id)).ToArray();
        var indexedTexts = _textObjects[index]
            .Select((text, at) => (Index: at, Text: text))
            .Where(entry => idSet.Contains(entry.Text.Id)).ToArray();
        var indexedFormulas = _formulaObjects[index]
            .Select((formula, at) => (Index: at, Formula: formula))
            .Where(entry => idSet.Contains(entry.Formula.Id)).ToArray();

        int total = indexedStrokes.Length + indexedImages.Length + indexedMedia.Length
            + indexedTexts.Length + indexedFormulas.Length;
        if (total == 0) return 0;

        var strokes = indexedStrokes.Select(entry => entry.Stroke).ToArray();
        var images = indexedImages.Select(entry => entry.Image).ToArray();
        var texts = indexedTexts.Select(entry => entry.Text).ToArray();
        var formulas = indexedFormulas.Select(entry => entry.Formula).ToArray();

        void Apply()
        {
            document.RemoveAll(strokes);
            foreach (var entry in indexedImages) _images[index].Remove(entry.Image);
            foreach (var entry in indexedMedia) _media[index].Remove(entry.Media);
            foreach (var entry in indexedTexts) _textObjects[index].Remove(entry.Text);
            foreach (var entry in indexedFormulas) _formulaObjects[index].Remove(entry.Formula);
            page.NotifyElementsChanged();
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("remove", index, images, null, texts, formulas);
        }

        void Revert()
        {
            document.InsertRange(indexedStrokes.Select(entry => (entry.Index, entry.Stroke)));
            foreach (var entry in indexedImages.OrderBy(entry => entry.Index))
                _images[index].Insert(Math.Clamp(entry.Index, 0, _images[index].Count), entry.Image);
            foreach (var entry in indexedMedia.OrderBy(entry => entry.Index))
                _media[index].Insert(Math.Clamp(entry.Index, 0, _media[index].Count), entry.Media);
            foreach (var entry in indexedTexts.OrderBy(entry => entry.Index))
            {
                if (!_textObjects[index].Contains(entry.Text))
                    _textObjects[index].Insert(Math.Clamp(entry.Index, 0, _textObjects[index].Count), entry.Text);
                entry.Text.Attach(this, page);
            }
            foreach (var entry in indexedFormulas.OrderBy(entry => entry.Index))
            {
                if (!_formulaObjects[index].Contains(entry.Formula))
                    _formulaObjects[index].Insert(Math.Clamp(entry.Index, 0, _formulaObjects[index].Count), entry.Formula);
                entry.Formula.Attach(this, page);
            }
            page.NotifyElementsChanged();
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("add", index, images, null, texts, formulas);
        }

        Apply();
        page.ClearSelection();
        page.History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "删除对象" : actionName.Trim(), Apply, Revert));
        return total;
    }

    /// <summary>
    /// 把文本/公式/图片/媒体设为同一 <paramref name="groupId"/>（空则自动生成），一次撤销。
    /// 返回命中的对象数量；已有分组被覆盖时撤销恢复原分组。
    /// </summary>
    public int GroupObjects(
        Guid pageId, IEnumerable<Guid> ids, string groupId = null, string actionName = null)
    {
        int index = ResolvePageIndex(pageId);
        if (index < 0) return 0;
        var idSet = ids?.Where(id => id != Guid.Empty).ToHashSet() ?? new HashSet<Guid>();
        if (idSet.Count == 0) return 0;

        string group = string.IsNullOrWhiteSpace(groupId) ? Guid.NewGuid().ToString("N") : groupId.Trim();
        var texts = _textObjects[index].Where(item => idSet.Contains(item.Id)).ToArray();
        var formulas = _formulaObjects[index].Where(item => idSet.Contains(item.Id)).ToArray();
        var images = _images[index].Where(item => idSet.Contains(item.Id)).ToArray();
        var media = _media[index].Where(item => idSet.Contains(item.Id)).ToArray();
        int total = texts.Length + formulas.Length + images.Length + media.Length;
        if (total == 0) return 0;

        var previous = new Dictionary<object, string>();
        foreach (BoardTextObject text in texts) previous[text] = text.GroupId ?? "";
        foreach (BoardFormulaObject formula in formulas) previous[formula] = formula.GroupId ?? "";
        foreach (BoardImageAsset image in images) previous[image] = image.GroupId ?? "";
        foreach (BoardMediaAsset asset in media) previous[asset] = asset.GroupId ?? "";

        if (previous.Values.All(value => value == group)) return 0;

        void ApplyGroup(string value)
        {
            foreach (BoardTextObject text in texts) text.GroupId = value;
            foreach (BoardFormulaObject formula in formulas) formula.GroupId = value;
            foreach (BoardImageAsset image in images) image.GroupId = value;
            foreach (BoardMediaAsset asset in media) asset.GroupId = value;
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("update", index, images, null, texts, formulas);
        }

        ApplyGroup(group);
        _pages[index].History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "分组对象" : actionName.Trim(),
            () => ApplyGroup(group),
            () =>
            {
                foreach (BoardTextObject text in texts) text.GroupId = previous[text];
                foreach (BoardFormulaObject formula in formulas) formula.GroupId = previous[formula];
                foreach (BoardImageAsset image in images) image.GroupId = previous[image];
                foreach (BoardMediaAsset asset in media) asset.GroupId = previous[asset];
                ImagesChanged?.Invoke();
                RaiseAiObjectsChanged("update", index, images, null, texts, formulas);
            }));
        return total;
    }

    /// <summary>清除同一分组内的全部对象分组（文本/公式/图片/媒体），一次撤销；返回对象数量。</summary>
    public int UngroupObjects(Guid pageId, string groupId, string actionName = null)
    {
        int index = ResolvePageIndex(pageId);
        if (index < 0 || string.IsNullOrWhiteSpace(groupId)) return 0;

        var texts = _textObjects[index].Where(item => item.GroupId == groupId).ToArray();
        var formulas = _formulaObjects[index].Where(item => item.GroupId == groupId).ToArray();
        var images = _images[index].Where(item => item.GroupId == groupId).ToArray();
        var media = _media[index].Where(item => item.GroupId == groupId).ToArray();
        int total = texts.Length + formulas.Length + images.Length + media.Length;
        if (total == 0) return 0;

        void ApplyGroup(string value)
        {
            foreach (BoardTextObject text in texts) text.GroupId = value;
            foreach (BoardFormulaObject formula in formulas) formula.GroupId = value;
            foreach (BoardImageAsset image in images) image.GroupId = value;
            foreach (BoardMediaAsset asset in media) asset.GroupId = value;
            ImagesChanged?.Invoke();
            RaiseAiObjectsChanged("update", index, images, null, texts, formulas);
        }

        ApplyGroup("");
        _pages[index].History.Push(new DelegateAction(
            string.IsNullOrWhiteSpace(actionName) ? "取消对象分组" : actionName.Trim(),
            () => ApplyGroup(""),
            () => ApplyGroup(groupId)));
        return total;
    }

    /// <summary>pageId 解析：Guid.Empty 表示当前页；未知页返回 -1 并写 [AI] 诊断日志。</summary>
    private int ResolvePageIndex(Guid pageId)
    {
        if (pageId == Guid.Empty) return CurrentIndex;
        int index = _pageIds.IndexOf(pageId);
        if (index < 0)
            Core.Diagnostics.FileLog.WriteLogToFile(
                $"[AI] 页令牌未知，命令忽略 pageId={pageId}", Core.Diagnostics.LogType.Info);
        return index;
    }
}