# 教学闭环模块接口、数据结构与配置说明

> 分支：`完善AI助教、课件联动与课堂练习闭环`｜日期：2026-11-21
> 范围：WinUI 3 C#（`src/`）；HarmonyOS 侧本次未改动。所有新增结构只追加字段并带版本号，旧数据可读。

## 1. 模块总览

| 模块 | 位置 | 职责 |
|---|---|---|
| `AssistantTeachingPolicy` | `Core/AiAssistant/AssistantTeaching.cs` | 四种讲题模式、答案/步骤裁剪、模式序列化 |
| `ProblemContext` / `ProblemContextPolicy` | `Core/AiAssistant/ProblemContext.cs` | 跨选区拼题：增删排序、外接矩形、首图兼容、序列化 |
| `SlideContext` / `SlideContextPolicy` | `Core/AiAssistant/SlideContext.cs` | 课件标题/本页/前后页文本上下文与提示词 |
| `AnnotationLayer` / `AnnotationLayerPolicy` | `Core/AiAssistant/AnnotationLayer.cs` | 解释步骤 ↔ 图形标注映射、逐步可见、定位降级 |
| `AiResponseGate` | `Core/AiAssistant/AiResponseGate.cs` | 会话 ID + 世代，丢弃过期流响应 |
| `TeachingSession` | `Core/Classroom/TeachingSession.cs` | 统一课堂会话与跨模块关联 |
| `StudentInteractionContracts` | ~~`Core/Classroom/StudentInteractionContracts.cs`~~ | P2 学生互动接口预留；死契约零消费者，已随重构删除 |
| `AiClassSummaryContracts` | ~~`Core/Classroom/AiClassSummaryContracts.cs`~~ | P2 AI 摘要草稿接口预留；死契约零消费者，已随重构删除 |
| `SlideContentIndex` | `Features/Ppt/SlideContentIndex.cs` | slideId → 批注/收藏/AI/缩略图/文本 |
| `HandoutComposer` / `HandoutExportService` / `HandoutPdfBuilder` | `Features/Ppt/HandoutExporter.cs`、`HandoutExportService.cs` | 选页、PNG 合成、最小 PDF |
| `ClassroomPreset` / `ClassroomPresetLibrary` | `Features/Classroom/ClassroomPreset.cs` | 场景预设与工具动作计划 |
| `VariantQuestionDraft` / `PracticePlan` / `ExerciseFlowPolicy` | `Features/Classroom/ExerciseFlow.cs` | 变式题草稿、练习计划、收尾记录 |
| `GroupSplitPolicy` / `GroupScoreService` | `Features/Classroom/GroupScoreService.cs` | 随机分组、积分、本轮重置、持久化 |
| `FocusRegionStore` | `Features/Classroom/FocusRegionStore.cs` | 多聚焦区域保存、排序、循环切换、序列化 |
| `PptInteropService.GetSlideText / GetAnimationStep / PauseMedia` | `Features/Ppt/PptInteropService.cs` | 课件页文本、动画点击阶段、媒体暂停（失败降级） |

App 层接线：`ShellController`（场景/练习/导出/聚焦贴图/AI 上下文/响应门）、`AssistantWindow`（模式、拼题缩略图、标注层、变式题入口）、`SlideNavigatorWindow`、`GroupScoreWindow`、`ToolkitWindow`、`SpotlightWindow`、`MagnifierWindow`。

## 2. 关键 API

### 2.1 讲题模式
```csharp
enum AssistantTeachingMode { Hint, StepByStep, Full, Teacher }
AssistantTeachingPolicy.Wire(mode) / Parse(text)
AssistantTeachingPolicy.VisibleSteps(mode, revealed, total)
AssistantTeachingPolicy.ShowsFinalAnswer(mode, revealed, total)
AssistantTeachingPolicy.HasMoreSteps(mode, revealed, total)
AssistantMachine.SetTeachingMode(session, mode)   // 不重发请求
AssistantMachine.RevealNextStep(session)
```

### 2.2 拼题上下文
```csharp
class ProblemRegion { Id, ImageBase64, Anchor, Text, Order, Source }
class ProblemContext { Version=1, SessionId, SlideId, SlideIndex, Regions, UpdatedAtMs }
ProblemContextPolicy.Add / Remove / Move / Ordered / CombinedAnchor / PrimaryImage
ProblemContextPolicy.DescribeSlideChange(context, slideId, slideIndex) // 切页保留提示
ProblemContext.Serialize / Deserialize
```

### 2.3 标注层
```csharp
class AnnotationHighlightItem { StepIndex, Index, Points(0..1), Locatable, Visible }
class AnnotationLayer { Version=1, CanLocate, Reason, Items }
AnnotationLayerPolicy.Build(session, anchor)
AnnotationLayerPolicy.SyncVisibility(layer, session)  // 与 RevealedSteps 同步
AnnotationLayerPolicy.SetAllVisible(layer, visible)
```
定位不可靠（锚点为空、点数 < 2）→ `CanLocate=false` + 中文原因，渲染层不绘制。

### 2.4 响应门
```csharp
var gate = new AiResponseGate();
int generation = gate.Begin(sessionId);
gate.IsCurrent(generation, sessionId);  // 流回调写入前检查
gate.Invalidate();                      // 关窗/换题
```

### 2.5 练习闭环
```csharp
enum ExerciseDifficulty { Basic, Advanced }
class VariantQuestionDraft { Version=1, Id, Difficulty, Text, Answer, KnowledgePoint, SourceSessionId, SlideId, SlideIndex }
class PracticePlan { Version=1, Id, ExerciseId, BoardPageIndex, CountdownMinutes, SceneId }
ExerciseFlowPolicy.BuildRequest(context, difficulty, sessionId, teaching) // purpose=variant
ExerciseFlowPolicy.Parse(aiText, difficulty, ...)
ExerciseFlowPolicy.Plan(draft, newBoardPageIndex, minutes)
ExerciseFlowPolicy.BuildPasteLines(draft)
// 录制耦合判定（书签名 / 结束快照 / isRecording）已移除：宿主只发 PracticeFinished 课堂信号，书签与快照决策归「时光机」插件
```

### 2.6 场景预设
```csharp
enum ClassroomTool { PptAnnotation, SlideNavigator, AiAssistant, AiStepGuide, Spotlight, Magnifier, Board, Countdown, GroupScore, HandoutExport, Toolkit }
enum ClassroomSceneKind { Teach, Practice, Group, Summary }
class ClassroomPreset { Version=1, Id, Name, Scene, IsBuiltIn, Tools, Hidden, CountdownMinutes, CollapseToolkit }
ClassroomPresetLibrary.BuiltIns / ForScene / ForId / Normalize / Plan / Serialize / Deserialize
class PresetAction { Open, Close, CountdownMinutes, CollapseToolkit }
```

### 2.7 分组积分
```csharp
enum GroupSplitMode { BySize, ByGroupCount }
GroupSplitPolicy.Split(names, value, mode, seed)
class GroupScoreService {
  ResetGroups(names, mode, value, seed);
  MoveMember(name, toGroupIndex);
  AddScore(groupIndex, delta);
  ResetRound();
  Ranking();
  BuildResultText();
  Serialize() / Deserialize(json);
}
```
不能整除：按人数时最后一组可少人；按组数时余数依次给前面的组。

### 2.7.1 聚焦区域
```csharp
class FocusRegionItem { Id, Name, X, Y, Width, Height, Order, SlideId, SlideIndex, AnimationStep, BoardPageIndex }
class FocusRegionStore {
  Add(item); Remove(id); Move(id, delta);
  Next(current, delta);            // 按讲解顺序循环
  FindMatching(x, y, width, height);
  Serialize() / Deserialize(json); // 最多 24 个
}
```
聚光灯窗口提供 `CurrentScreenRect`、`SetRegion(ScreenRect)`、`SaveRegionRequested`、`CycleRegionRequested`。

### 2.8 讲义导出
```csharp
enum HandoutContentMode { AnnotationsOnly, AnnotationsWithTeacherNotes }
class HandoutOptions { Version=1, Content, SelectedSlideIds, PageWidthPt=595.28, PageHeightPt=841.89 }
HandoutComposer.Select(pages, options) / PageSize(options) / DescribeMode(mode)
HandoutExportService.ExportAsync(HandoutRenderRequest, outputDirectory) // 失败重试一次
HandoutPdfBuilder.Build(IReadOnlyList<HandoutPdfPage>, pageWidthPt, pageHeightPt)
HandoutImageDecoder.DecodePngAsync(png) // PNG → RGB
```
输出目录：`<我的图片>\InkCanvas\handout-<yyyyMMdd-HHmmss>\`，逐页 `handout-<页码>.png` + `handout.pdf`。

## 3. 数据结构（JSON）

### 3.1 `ProblemContext`
```json
{
  "Version": 1,
  "SessionId": "…",
  "SlideId": 7,
  "SlideIndex": 2,
  "Regions": [
    { "Id": "1", "ImageBase64": "…", "Anchor": { "Left": 1, "Top": 2, "Right": 3, "Bottom": 4 },
      "Text": "", "Order": 1, "Source": "selection" }
  ],
  "UpdatedAtMs": 0
}
```

### 3.2 `TeachingSession`
```json
{
  "Version": 1, "Id": "…", "Name": "课堂",
  "StartedAtMs": 0, "UpdatedAtMs": 0,
  "PresentationIdentity": "", "PresentationName": "",
  "PresetId": "teach",
  "CurrentSlideId": 0, "CurrentSlideIndex": 0, "CurrentAnimationStep": 0,
  "CurrentBoardPageIndex": -1,
  "AiSessionIds": [], "ExerciseIds": [], "ExportPaths": [],
  "Links": [
    { "Kind": "ai|exercise|tool", "Id": "…", "SlideId": 0, "SlideIndex": 0,
      "AnimationStep": 0, "BoardPageIndex": -1, "CreatedAtMs": 0, "Note": "" }
  ]
}
```
课件页键：`slide:{slideId}@{slideIndex}#{animationStep}`。

### 3.3 `SlideContentIndex`
```json
{
  "Version": 1, "Identity": "…", "Name": "…", "UpdatedAtMs": 0,
  "Slides": [
    { "SlideId": 7, "Index": 2, "Name": "…", "HasInk": true, "InkStrokeCount": 3,
      "Favorite": false, "HasAi": true, "ThumbnailPath": "…/thumb-7.png", "Text": "AI 讲解/页文本" }
  ]
}
```
存储：`<课件目录>/SlideIndex.json`；缩略图 `<课件目录>/thumb-{slideId}.png`。

### 3.4 其他持久化
| 数据 | 路径 | 版本 |
|---|---|---|
| 班级名单 | `%APPDATA%\InkCanvas\roster.json` | 既有 |
| 分组积分 | `%APPDATA%\InkCanvas\groups.json` | `GroupScoreState.Version=1` |
| 课堂会话 | `%APPDATA%\InkCanvas\teaching\session-<id>.json` | `TeachingSession.Version=1` |
| 课件页索引 | `<课件目录>\SlideIndex.json` | `SlideContentIndex.Version=1` |
| 设置 | `%APPDATA%\InkCanvas\Settings.json` | `classroom.version=1` |

## 4. 配置（`Settings.json` → `classroom`）

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `version` | int | 1 | 结构版本 |
| `presetId` | string | `teach` | 上次使用的场景 |
| `countdownMinutes` | int | 3 | 练习/场景默认倒计时 |
| `toolkitCollapsed` | bool | false | 工具箱下次打开时的收起状态 |
| `practicePageName` | string | `随堂练习` | 新建练习页名称 |
| `customPresets` | string(JSON) | `""` | 教师自定义预设序列化 |
| `groupSplitMode` | string | `groupCount` | `groupCount`/`size` |
| `groupSplitValue` | int | 4 | 组数或每组人数 |
| `spotlightRegions` | string(JSON) | `""` | 多聚焦区域（`FocusRegionStore`） |
| `toolkitX` / `toolkitY` | int | `int.MinValue` | 工具箱屏幕位置；未保存时默认居中 |
| `magnifierX` / `magnifierY` | int | `int.MinValue` | 放大镜屏幕位置；未保存时默认居中 |

旧版兼容字段（本次补齐，读写但不改变行为）：
- `canvas.penNib`（int）、`canvas.highlighterWidth`（double?）；
- `experimental` 节点（`ScreenContentTracking`/`FollowScroll`/`FollowZoom`/`MaskFixedRegions`/`RememberPages`/`PreserveUnchangedRegions`/`Word`/`PowerPoint`/`Edge`）。

AI 配置新增/沿用：`ai.teachingMode`（`hint`/`step`/`full`/`teacher`，默认 `step`）。

## 5. AI 请求扩展

`AssistantRequest` 追加（既有字段顺序与含义不变）：
- `images[]`：跨选区图片（`id`/`order`/`imageBase64`/`rect`）
- `teaching`：`hint|step|full|teacher`
- `purpose`：空=普通讲题，`variant`=变式题
- `difficulty`：`basic|advanced`
- `slideContext`：`{ version, title, slideId, slideIndex, animationStep, currentText, previousText, nextText, selectionText, prompt }`

服务端约束写入 `prompt`：课件文本仅为辅助，冲突时以用户框选为准。

## 6. 扩展点（P2，未实现）

- `IStudentInteractionService`：投票/选择题/举手/提交答案；离线返回 `Offline`，不影响本地授课。
- `IAiClassSummaryService`：返回 `ClassSummaryDraft`（可编辑、含来源引用）；未接入时不生成。

## 7. 验证边界

已实机 E2E 覆盖：AI 四模式（分步默认不泄露、逐步揭示、完整显示公式）、点名→分组积分→加分→贴板、画板+倒计时→结束信号→「时光机」插件 timer 书签；截图与日志在 `artifacts/teaching-loop-e2e/`（历史记录，书签链路以插件验收为准）。构建 0 警告 0 错误，Core 148/148、Ai 132/132、P0 33/33、Classroom 252/252 全通过（0 跳过，重构前基线）。

仍**未验证**：真实 PowerPoint/WPS 的页文本/动画步/媒体暂停、真实 AI 端点、讲义真实排版与导出全链路、变式题/聚焦贴图/讲解回放/预设保存等 UI 完整点击、高 DPI/高对比度、多屏与投屏。90 分钟压力归属「时光机」插件测试。详见 `docs/teaching-loop-gap.md` 第 9 节；单元测试与构建证据只覆盖纯逻辑与编译，不等同于实机功能验收。