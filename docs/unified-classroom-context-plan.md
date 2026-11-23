# 架构现状与迭代差距清单（统一上下文 / 对象 / 命令 / 课堂闭环）

- 分支：`统一板书对象模型并完善AI课堂教学闭环`（基线 `a27a725`，已合并云端 v3.10.0）
- 审查方式：只读静态审查（上下文路由、对象/命令、三个闭环），未修改代码
- 结论：现有能力可复用度高，但**上下文只到"画布+页索引"、对象/命令/序列化/渲染/回放存在多套并行实现**；本方案以增量收敛为主，不推倒重写

## 1. 现状审查摘要

### 1.1 上下文与 AI 写回（真实风险）

- `AssistantTarget` 仅 `Overlay/MainBoard/SideBoard + PageIndex + 进程内引用`；无 `boardId/pageId/slideId/selectionId/taskId/source`。
- `ShellController.cs:5449` 把请求快照的 `SessionId` 改写成当前会话 → 会话校验恒真。
- Overlay/PPT/PDF 文档目标只比 `Kind`：PPT 翻页、切文档、桌面覆层仍视为同一目标；贴回恒落主展板。
- `ShellController.cs:4804` 空间不足弹窗 await 后**无复检**，弹窗期间翻页会写入错误页。
- 子板显示时 `ActiveController` 强制为子板；子板翻页不通知 AI/录制；子板隐藏期间仍可"贴回"到不可见目标。
- 主板关闭不终止 AI 流 → 逐 token 弹过期提示；关闭浮窗不清理 `_aiTarget/_aiBoardPlan`。
- 时光机 `BoardId` 恒 0、AiStep `PptSlideId` 恒 0，生产端不录制子板。

### 1.2 对象与命令

- 对象类型：`InkStroke / BoardImageAsset / BoardMediaAsset / BoardTextObject / BoardFormulaObject / 形状 / 批注 / 旧 AI 卡片`；选择契约仅文本/公式直接实现，图片/媒体靠适配器，笔画走独立 Selection 通道。
- 页面结构由 5-7 个并行 List 维护（`_pages/_images/_media/_textObjects/_formulaObjects/_states/_papers`），增删页需同步全部。
- 命令分散：Create/Edit/Transform/Delete/Group/Paste 各有 2-4 套实现（`DeleteSelection` vs `RemoveAiObjects` vs `RemoveImage`；`UpdateAiObject` vs `UpdateImage`；`RecordImageTransform` 旧接口；分组 Guid vs string；贴回 PNG 化）。
- 序列化 4 套：Board v4 `StrokeFile`、`.icink InkFileStore`、时光机 Core `StrokeJsonCodec`（字段缺失）、Modules `StrokeJsonCodec`（全字段）。
- 渲染内核同源（`InkSceneRenderer`），但页面合成（背景/网格/媒体海报/边界）各写一份；子板贴回边界漏文本/公式。
- 时光机对象无 `Transform`（旋转/翻转回放失真）；UI 删除 AI 对象不触发特效事件。

### 1.3 课堂闭环与恢复

- 讲题：四档教学模式/步骤裁剪/局部纠错/图形标注/贴回/收藏已具备；贴回仍为 PNG 卡片。
- 练习：变式生成/预览/新页/倒计时/书签在；练习页仍 PNG 卡片；`PracticeFinishRecord.SnapshotPath` 未持久化；无"下一题/收题"。
- 总结：board_plan + 原文/结果对照/部分采用 + 原生写入已具备；`summary` 块被降级为 text；"总结页"无专用实现；教师修改后 `Source` 仍为 ai。
- 例题库：无 AiSessionId/ObjectIds/截图/步骤；UI 仅检索/删除。
- 恢复：`SessionJournal`/`LoadWithJournal` 服务存在但**无 UI 入口**；插件录制仅 Stop 时落盘，无增量保存。
- 90 分钟仅有合成压测（journal 54000 事件、30fps 162001 帧），真实课堂未验证。

## 2. 统一模型设计（最小改造面）

### 2.1 ContextSnapshot（P0）

```csharp
public enum BoardCanvasKind { Overlay, PptOverlay, Document, MainBoard, SideBoard }
public enum ContentSource { UserInk, AiOrganize, AiWrite, AiShape, PptAnnotation }
public sealed record ClassroomContext {
    string SessionId; string RequestId; int Generation;
    BoardCanvasKind Kind; string BoardId; Guid PageId; int PageIndex;
    string? PresentationKey; int? SlideId; int? SlideIndex; int? AnimationStep;
    string? SelectionId; int[]? StrokeIds; string? GroupId; string[]? ObjectIds;
    ContentSource Source; long CreatedAtMs;
}
```

- 比较策略：`SameBoard(Id) → SamePage(PageId) → SameSlide(PresentationKey,SlideId,AnimationStep) → SameSelection/Generation`；写回要求全部通过，显示仅要求 Board/Page。
- `PageId` 持久化到 `BoardPageFile`（可选字段，缺失时进程内生成），解决"同索引换页 ≠ 同页"。
- `RequestId` 每次请求生成，贯穿截图/流/写入/AiStep 日志。

### 2.2 对象与命令（P0 最小收敛）

- `IBoardObject` 只读视图：`Id/Type/Content/Style/Rect/RectTransform/Context/Provenance/Version`；`InkStroke` 通过 `StrokeObjectAdapter` 适配，文本/公式直接实现，图片/媒体适配器补齐。
- `ObjectProvenance`：`SessionId/RequestId/BoardId/PageId/SlideId/Source/Instruction/CreatedAt/GroupId`；写入 `AiPasteMetadata v2` 与 `AiObjectsPayload`（新增字段，保持旧字段可读）。
- 统一命令入口（薄封装现有实现，不重写）：
  - `CreateObjects(pageId, objects)` → `AddAiObjectsAtomic/AddImagesAtomic`；
  - `EditObjects(pageId, before/after)` → `UpdateAiObject/UpdateImage`；
  - `TransformObjects(pageId, ids, matrix)` → `InkController.TransformSelectionUndoable`；
  - `DeleteObjects(pageId, ids)` → 统一触发 `ImagesChanged + AiObjectsChanged`；
  - `GroupObjects/ Ungroup`；`PasteObjects`（子板贴回优先复制对象，保留"贴为图片"选项）。
- 所有命令落 `InkController.History`（一次 `DelegateAction`），统一发对象事件 → 保存/回放/导出共享同一事件源。

### 2.3 迁移与回滚

- `BoardPageFile` 增加可选 `PageId/ObjectsVersion/Provenance`；`BoardFile.Version` 保持 4，双读兼容（缺字段生成默认）。
- `AiStep` payload 升 `schema=3`：新增 `transform/source/slideId/boardId`；旧 schema=2 读取兼容（transform 缺失回退 identity 并标记）。
- `Core.StrokeJsonCodec` 复用 Modules 全字段（缺省兼容旧 JSON）。
- Feature Flag：`features.unifiedContext`（默认开，可关回旧路由）；`features.nativePractice`（练习原生对象）。
- 迁移前自动备份：`<file>.bak`（已有机制复用）；失败回滚到备份。

## 3. 分阶段计划（每阶段可运行、可回退）

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 | 统一上下文 `ClassroomContext` + PageId + 写回复检 + 子板/PPT/过期校验 + Stop/关闭清理 + 录制 BoardId/SlideId + Feature Flag | 上下文单测；主/子板/PPT 翻页不串；弹窗期间翻页拒绝写入 |
| 2 | 对象/命令收敛 + 时光机 transform + 删除入时间轴 + 讲义页渲染统一 + 原始板书保护/连续修改 + 教学评测集 fixtures | 命令一次撤销；回放旋转对象一致；评测 metrics.json |
| 3 | 讲题/练习/总结闭环：例题关联与再讲、练习原生对象与快照持久化、总结页与来源标记、讲义预览 | 三闭环端到端测试 |
| 4 | 时光机确定性（多路径同帧）、90 分钟真实压测、异常恢复 UI、例题库/预设/资源包、清理 | 一致性/压测/恢复报告 |

## 4. 本期先做的阶段 1 范围

1. Core：`ClassroomContext`/`ContextPolicy`/`PageId`/`Provenance`（纯逻辑 + 单测）。
2. App：`CaptureAssistantTarget` 生成完整快照；删除 SessionId 自改写；`CanWriteBack` 校验 board/page/slide/selection/generation；确认弹窗后复检；关闭浮窗/停止/主板关闭清理；子板翻页通知。
3. 录制：主/子板各自录制器绑定 BoardId；AiStep 带 board/slide/source。
4. Feature Flag `unifiedContext`（默认开；异常可关回旧行为）。
5. 回归：Core/Ai/P0/Classroom/TimeMachine 全量。

## 5. 未验证与风险

- 真实 AI 端点、真实 PPT/WPS、多屏/系统 DPI、真实课堂 90 分钟未验证。
- 旧 v2/v3 内联媒体包、旧会话 schema=2、旧 AI 卡片迁移需实机样本验证。
- 统一模型若替换 `TextObjects/FormulaObjects` 字段需双写双读一个版本周期；本期先"新增可选字段 + 适配器"，不改既有字段语义。
**2026-11-23 用户确认**：真实 AI 端点、真实 90 分钟课堂、真实 PPT/WPS、多屏/系统高 DPI、三闭环完整实机操作、资源包 UI 打包解包、恢复“恢复”分支、旧 v2/v3 与旧 schema=2 样本、子板录制回放等，均作为**未验证项**保留，由用户后续自行验证；本轮不声称达成。
**2026-11-23 实机补充证据**：
- 总结闭环：AI 浮窗「AI 书写」默认指令“写一句本课总结” → mock 返回 `summary` 计划 → 预览 → 全部采用 → 自动新建并命名「课堂总结」页（页面总览显示“2. 课堂总结 / 共 2 页”），原始页保留。
- 启动恢复：检测到未完成录制并提示「恢复/放弃」，选择放弃后提示“已放弃 42 个未完成录制，删除的只是恢复残留”。
- 预设窗口：板书笔色/页面背景/页面模板/练习时长/AI 指令等新控件完整、布局无裁切。- 练习变式：板内画一笔→选择→AI 助教→基础变式，实机浮窗显示“题目/答案/知识点”（mock 变式剧本）；但“预览/开始练习”对话框未弹出（受选区题目上下文限制），练习闭环完整流程仍未实机验证，保持未验证项。
- 压测判据：`LongPlaybackStressTests` 已恢复原判据“峰值−结束 < 256MB”（仅测量前 GC 稳定，阈值/帧数/180s 预算未改）；Debug 内存差 0.0MB、耗时 185s（超 80ms，机器性能边界）；Release 147s、371/371 通过。不声称 Debug 环境通过。