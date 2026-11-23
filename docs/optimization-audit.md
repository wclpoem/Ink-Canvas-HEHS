# 现状审查与优化差距清单（WinUI 端）

> **【历史归档 · 2026-11-20 审计记录】** 本文 §3「时光机」小节为本体阶段的差距与实现状态。时光机已重构为独立插件模块「时光机」（Id `inkcanvas.time-machine`）：其中增量 journal、异常退出恢复、`.sha256` / `.bak` 校验备份与 WASAPI 音频条目随本体移除；统一时间轴、自动书签、抽稀、检查点定位与 MP4 导出管线等可复用结论由插件侧沿用复核。AI 助教与课件联动小节为同期历史记录。

> 审查日期：2026-11-20
> 范围：AI 助教（Core/AiAssistant、Platform/Ai、Features/AiAssistant、App/Views/AssistantWindow）、课件联动（Features/Ppt、Platform/Windows）、课堂工具箱与时光机（Features/Classroom、Core/Classroom、App/Views）。
> 结论先行：**课件联动的 slideId 锚定已基本落地**；主要缺口集中在 AI 的可编辑/自检、课件旧数据迁移与邻页缓存、时光机的统一时间轴/书签/抽稀/导出、以及全局 Feature Flag 与性能度量。

## 1. AI 助教（2.1）

| 编号 | 现状（定位） | 根因 | 影响 | 改造方案 | 工作量 | 优先级 |
|---|---|---|---|---|---|---|
| A1 局部纠错 | `AssistantWindow.Apply` 全量重建正文；`ShellController.OpenAiAssistant` 每次新建会话；请求只有整张截图（AssistantCapture 全选区） | 会话/请求模型没有“局部区域 + 替换范围”；UI 无圈选交互 | 错一个符号只能重开整题，流式回答丢失 | Core 增加 `AssistantRegion`（viewport rect + 文本区间）与 `AssistantReplace`；Platform 请求增加 `region/previousText/replaceRange`；浮窗缩略图支持拖框；返回仅替换目标段落，其余保留 | M | P0 |
| A2 贴回可编辑 | 贴回仅生成 PNG：`BoardImageAsset{ContentType="image/png"}`（ShellController.cs:1229-1238）；无 LaTeX 元数据 | BoardImageAsset 无元数据字段；BoardWindow 无双击编辑；无矢量标注对象 | 贴回后不可改；图形释义与墨迹混在一起 | BoardImageAsset 增加 `Metadata`（JSON：kind/latex/strokes/annotations），格式版本+1 且向后兼容；BoardWindow 双击打开编辑（公式回到输入框，图形作为独立矢量层可移动/删除） | L | P0 |
| A3 求解自检 | `AssistantEvent` 无置信度/验证字段；UI 直接展示公式（AssistantWindow.cs:490） | 无本地验证器，服务端也无约定字段 | 错误答案静默输出 | Core 新增 `EquationVerifier`（线性/一元二次/可代入表达式），`AssistantEvent` 增加 `Confidence/Verified`；卡片显示“已验证/未验证/低置信” | M | P0 |
| A4 流式优化 | 截图压缩已有（AssistantCapture maxEdge=1600）；超时已可配（AI_TIMEOUT_MS）；请求一次性发送截图+墨迹（AiClient.cs:75-80） | 无“墨迹先行/图片后补”两阶段；无弱网本地公式转写 | 首字延迟受图片上传拖累；弱网无降级内容 | 拆两阶段请求：先 `inkStrokes`+轻量提示，后补 `imageBase64`；本地 `LocalFormulaTranscriber`（基于现有识别/文本规则）先展示；超时按配置分阶段 | M | P0 |

### 状态复核（2026-11-20）

| 编号 | 状态 | 对应文件 / API | 对应测试 |
|---|---|---|---|
| A1 局部纠错 | 已实现·单测通过；圈选 UI 未实机 | `Core/AiAssistant/AssistantReplace.cs`、`AssistantRegion`、`AiClient` 请求体 `region`、`ShellController` 圈选开关 | `AiOptimizationTests.Replace_*`、`ApplyRegionResult_UsesRegionOffsets` |
| A2 贴回可编辑 | 已实现·单测通过；双击编辑 UI 未实机 | `Features/AiAssistant/AiPasteMetadata.cs`、`BoardImageAsset.Metadata`、Board v3 | `Serialize_RoundTripsFormulaMetadata`、`SaveLoadV3_PreservesImageMetadata`、`UpdateImage_*` |
| A3 求解自检 | 已实现·单测通过；UI 徽标未实机 | `Core/AiAssistant/EquationVerifier.cs`、`AssistantEvent.Confidence/Verified` | `Verifier_*`（8 项）、`ApplyEvent_Formula_SyncsConfidenceAndVerified` |
| A4 流式优化 | 已实现·单测通过；mock 首字已实测，真实端点未验证 | `AiClient.StartTwoStage`、`LocalFormulaTranscriber`、`AiConfig.TimeoutForStage` | `Payload_InkOnly_OmitsImage`、`Config_SelectsTimeoutByStage`、`Transcriber_*`、`PerformanceBench.Bench_AiClient_MockFirstTokenLatency` |

## 2. 课件联动（2.2）

| 编号 | 现状（定位） | 根因 | 影响 | 改造方案 | 工作量 | 优先级 |
|---|---|---|---|---|---|---|
| P1 slideId 锚定 | **已基本实现**：`_slides[SlideId]`、`ApplySlide` 用 `SlideId`（PptInkSession.cs:225,239）、存储 `slide-{SlideId}.icink`（PptInkFileStore.cs:88）；但 `_lastSlideIndex` 仍按页码恢复（PptInkSession.cs:185,258） | 页码仍作为恢复位置与降级字段 | 插页后“恢复上次位置”可能指错页 | 恢复位置改为 `LastSlideId`（保留 `LastSlideIndex` 降级）；manifest 增加版本号 | S | P0 |
| P2 迁移与恢复向导 | 有 `LegacyInkFilesDetected`（PptInkFileStore.cs:35-36）但无迁移；manifest 无版本；无备份 | 旧数据按页码/名称目录，无映射与回滚 | 旧批注可能丢失或串页 | 新增 `SlideInkMigrator`：旧目录→按当前 details 顺序映射 SlideId，写 manifest v2，原目录 `.bak`；`SlideMappingInspector` 失配时弹恢复向导（保留/重映射/导出/清空） | L | P0 |
| P3 邻页预热/过期丢弃 | `PptInkSession` 同步读写 `_slides`，无前后页缓存；`SlideSwitchGate` 仅存在于会话层且未用于加载 | 无缓存层；切页同步完成但首次加载可能抖动 | 快速翻页可能卡顿/串页 | 新增 `SlideInkCache`（前后各 1 页，LRU 上限 8）；切页用 session/generation 丢弃过期加载；同帧卸载旧页+合成新页 | M | P0 |
| P4 降级矩阵显式化 | 已有 COM→`PresentationFallbackDetector`→`KeyboardInjector`（ShellController.cs:313 等），但为分支代码，无独立策略对象 | 未抽象、不可单测 | 任一级失效难定位，回归风险 | 抽 `IPresentationSource`（Com/Window/Key 三实现），状态机记录 `ActiveLevel/Failures`，日志与单测矩阵 | M | P0 |
| P5 翻页笔/黑屏 | 无翻页笔与黑屏逻辑；仅有全局热键 Alt+… | 未接入演示器键与黑屏层 | 演示器翻页不可用；无法一键遮蔽课件 | 监听 PageUp/PageDown/F5/B/句点（RegisterHotKey/低级钩子）；新增 `BlackScreenWindow`（全屏黑，不销毁批注；恢复即隐藏） | M | P0 |

### 状态复核（2026-11-20）

| 编号 | 状态 | 对应文件 / API | 对应测试 |
|---|---|---|---|
| P1 slideId 锚定 | 已实现·单测通过 | `PptInkSession`（`SlideId` 恢复）、`PptInkFileStore`（manifest `LastSlideId`）、`PptModels` | `SlideMigrationTests.ResumeIndex_PrefersSlideIdOverPageNumber`、`SaveAndLoad_RoundTripLastSlideId`、`ManifestWithoutLastSlideId_LoadsVersion2AndKeepsIndexFallback` |
| P2 迁移与恢复向导 | 已实现·单测通过；向导 UI 未实机 | `SlideInkMigrator`（manifest v2、`.bak-<timestamp>`）、`SlideMappingInspector`、`ShellController.ShowInkRecoveryWizardAsync` | `LegacyManifestWithoutVersion_MigratesToVersion2AndMapsSlideIds`、`Migration_KeepsLegacyIsfFilesAndWritesBackup`、`Inspector_*`、`Migration_WithLargeMissingRatio_ReportsSkippedWithoutClearing` |
| P3 邻页预热/过期丢弃 | 已实现·单测通过；预取线程实机未验证 | `SlideInkCache`（LRU 8、深拷贝）、`PptInkSession.PrefetchNeighbors`、`SlideSwitchGate` | `BatchBTests.Cache_*`（7 项）、`PerformanceBench.Bench_SlideInkCache_HitVsMiss_100Iterations` |
| P4 降级矩阵显式化 | 已实现·单测通过；真实三级降级未实机 | `PresentationSources.cs`（`PresentationSourceSelector`：Com → Window → Keys → None） | `BatchBTests.Selector_*`（6 项） |
| P5 翻页笔/黑屏 | 已实现·单测通过；黑屏/翻页键未实机 | `PresentationKeyMap`、`BlackScreenWindow`、`ShellController.ToggleBlackScreen`、`OverlayHost` | `BatchBTests.KeyMap_*`（4 项） |

## 3. 时光机（2.3）〔历史归档：本节条目对应本体实现；插件化后恢复 / 校验 / 备份 / 音频条目关闭，时间轴 / 书签 / 抽稀 / 导出结论由插件沿用〕

| 编号 | 现状（定位） | 根因 | 影响 | 改造方案 | 工作量 | 优先级 |
|---|---|---|---|---|---|---|
| T1 统一时间轴 | `InkRecorder` 只记录墨迹/页/背景（Features/Classroom/InkRecorder.cs）；PPT 翻页/批注、点名、倒计时未入会话 | 事件源未统一；`ClassroomSession` 类型单一 | 回放不能还原“当时画面”（课件页/点名/计时） | 新增 `ClassroomTimeline` 统一事件（Ink/Ppt/Roster/Timer）；Session v2 增加 `Source` 与 `PptSlideId`；回放组合渲染 | L | P0 |
| T2 自动书签 | 无书签；进度条仅 Slider | 会话模型无 Bookmark | 长课难定位；无法手动命名 | `SessionBookmark{TimeMs,PageIndex,Name,Auto}`；切页/清空/切画板自动生成；进度条标记 + 点击跳转 + 重命名 | M | P0 |
| T3 长课性能 | 检查点已有（200 事件/10s）；**无抽稀**；会话仅在停止时整包保存（ClassroomSessionStore.Save） | 无点抽稀；无增量 journal | 90 分钟会话体积大、异常退出丢失整节课 | `StrokeSimplifier`（RDP，容差可配，回放视觉无损）；`SessionJournal` 追加写（每 2s/50 事件 flush）；检查点按时间间隔 | M | P0 |
| T4 视频导出 | 无导出；仅 JSON 导出（ClassroomSessionStore.Export） | 无帧渲染/编码管线 | 无法产出微课视频 | Media Foundation `SinkWriter`（H.264 MP4）+ 离线帧渲染（复用回放渲染）+ 可选 WASAPI 音频；后台任务不阻塞书写 | XL | P0（可分期） |

### 状态复核（2026-11-20）

| 编号 | 状态 | 对应文件 / API | 对应测试 |
|---|---|---|---|
| T1 统一时间轴 | 已实现·单测通过 | `TimelineEvent`（Ink/Ppt/Roster/Timer/Bookmark）、`InkTimeline.SceneAt`、`InkRecorder.Append` | `TimeAxisTests.RecorderAppend_*`（3 项）、`SceneAt_*`（3 项） |
| T2 自动书签 | 已实现·单测通过 | `InkTimeline.AutoBookmarks/ApplyAutoBookmarks`、`SessionBookmark` | `AutoBookmarks_*`（5 项）、`Bookmarks_*`（2 项）、`SessionV2_SaveLoad_RoundTripsBookmarks` |
| T3 长课性能 | 已实现·单测通过；抽稀与回放定位已实测 | `StrokeSimplifier`（RDP 0.8）、`SessionJournal`（2s/50 事件 flush）、`InkTimeline` 检查点 | `BatchCTests.Simplify_*`（8 项）、`Journal_*`（5 项）、`Checkpoint_*`、`PerformanceBench.Bench_StrokeSimplifier_10000Points`、`Bench_InkTimeline_BuildFrameFromCheckpoint` |
| T4 视频导出 | 代码已实现；参数单测通过；MP4 实机编码与音频未验证 | `TimeMachineExporter`（Media Foundation H.264）、`TimeMachineWindow.RunExportAsync` | `ExportOptions_ValidateRejectsBadRangesAndPaths`、`ExportOptions_SampleFrameTimes_RespectsFpsAndBounds` |

## 4. 跨模块工程

| 编号 | 现状 | 方案 | 工作量 |
|---|---|---|---|
| F1 Feature Flag | Settings 中无 AI/优化开关 | 新增 `AiSettings/ClassroomSettings` 开关（默认 true）；关键分支包裹；配置界面可回退 | S |
| F2 数据版本 | Board v2 已有；Ppt manifest 无版本；Session JSON 无版本 | 全部加 `Version` + 迁移函数 + 旧文件只读兼容 | S-M |
| F3 测试基线 | `tests/InkCanvas.Core.Tests/VectorParityTests.cs` 引用 src 不存在 API，**既有测试项目不可编译**；已用独立项目（P0/Ai/Classroom）覆盖 | 需用户决定：接受“新增测试全过 + 基线欠账登记”，或授权补实现缺失 API | 决策项 |
| F4 性能度量 | 无基准脚本；真实 AI 端点未配置；本机仅 Office 2021 + WPS，无多版本矩阵 | 提供可复现的本地基准（合成数据/mock）并如实标注真实项未验证 | M |

### 状态复核（2026-11-20）

| 编号 | 状态 | 对应文件 / API | 验证情况 |
|---|---|---|---|
| F1 Feature Flag | 部分：`AiSettings` 4 项已实现；`ClassroomSettings` 未新增 | `Core/Settings/AppSettings.cs:AiSettings`、`ShellController` 4 处开关分支 | 无专门单测；默认值/回退分支已核对 |
| F2 数据版本 | 已实现·单测通过 | Board v3、Ppt manifest v2、Session v2、journal/snapshot | A2/P2/T3 单测本次复跑通过 |
| F3 测试基线 | 部分（欠账登记）：既有基线仍不可编译 | `tests/InkCanvas.Core.Tests` 未修改 | 本次 `dotnet build` 失败；独立项目 Ai 53/53、Classroom 130/130 通过 |
| F4 性能度量 | 部分：合成/mock 已实测 | `tests/InkCanvas.Classroom.Tests/PerformanceBench.cs` | 5 个基准测试通过、8 条 `[PERF]`；真实端点/渲染未验证 |

## 5. 建议分期

- **P0-A（AI）**：A1 局部纠错 → A2 贴回元数据/编辑 → A3 自检 → A4 流式两阶段（约 2 个迭代）
- **P0-B（联动）**：P1 恢复位置改 slideId → P2 迁移/向导 → P3 邻页缓存 → P4 降级矩阵 → P5 黑屏/翻页笔
- **P0-C（时光机，历史路线）**：T1 统一时间轴 → T2 书签 → T3 抽稀/增量 → T4 视频导出；插件化后该路线由「时光机」插件承接，恢复 / 校验 / 音频条目关闭。
- **P1**：两次框选合并、倒计时快照书签、点名贴回、快捷键表、内存治理

## 6. 验收口径待确认

1. 既有测试基线不可编译（与本需求无关），是否接受“新增/可运行测试全过，基线欠账单独登记”？
2. 真实 AI 首字 ≤2s、90 分钟课堂、PPT/WPS 多版本矩阵无法在本机全量复现，是否接受“可复现基准 + 模拟数据，真实项标注未验证”？
3. 回放视频导出：先交付 Media Foundation MP4，还是先帧序列/GIF（不含音频）？
4. P0 三块是全部一次交付，还是按 A→B→C 分批验收？