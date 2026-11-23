# WinUI 端优化实施报告（AI 助教 / 课件联动 / 课堂时光机）

> 复核日期：2026-11-20
> 代码基线：`D:\hw\Ink-Canvas-HEHS`（未提交工作区）
> 验证环境：Windows 11 Home China · .NET SDK 10.0.404 · xunit 2.9.2 · Debug · x64
> 运行时变量：`DOTNET_ROOT=D:\dotnet`、`NUGET_PACKAGES=D:\dev-cache\nuget`
> 结论口径：**单测与构建结论以本次实际执行为准（Ai 79/79、Classroom 164/164、App Debug x64 0 警告 0 错误）；黑屏/Esc、双击编辑 AI 卡片、局部圈选端到端（圈选 → RegionSelected → 定点替换，附日志链）、图形释义独立矢量图层、60 次连续翻页（30,465 ms、页码失配 0、切页 >50 ms 长任务 0、Error 0）已验证；残影/闪烁仍无客观量化，真实 AI、WPS、多屏/DPI、真实 PPT 截图回放 UI、90 分钟课堂等未实机项仍标注「未验证」，不声称通过。**
>
> **【历史归档 · 时光机部分】** 本文 C 组「时光机」为本体阶段实施记录。时光机已重构为独立插件模块「时光机」（Id `inkcanvas.time-machine`）：WASAPI 讲解音频录制与 AAC 音轨合入、journal / 快照异常恢复、会话存储与校验备份条目已随本体移除，不迁入插件；时间轴、书签、抽稀、检查点定位与 MP4 纯视频导出管线由插件沿用复核。下列 C 组各行的状态、用例与数字均为重构前历史基线。

---

## 1. 实施范围与状态

状态说明：
- **已实现·单测通过**：代码存在，且本次运行对应自动化测试通过。
- **已实测**：本次实际执行的合成基准或构建命令产生了真实数字。
- **已实测通过**：在真实 App 窗口/系统编码器上执行并观察到预期现象，有可复核的产物或界面证据。
- **未实机**：代码路径存在或仅有单测，但未在真实 UI/硬件/外部软件环境运行。

### 1.1 A 组：AI 助教（审计 2.1）

| 编号 | 状态 | 实现文件 / API | 关键单测 |
|---|---|---|---|
| A1 局部纠错 | **已端到端实测成功**（圈选 → RegionSelected → 定点替换）；定点替换另有 14 项端到端逻辑测试 | `Core/AiAssistant/RegionTextMapper.cs`（rect 比例 → 字符区间 `Map`/`Apply`）、`LocalCorrectionFlow.Apply`（克隆会话、仅替换目标区间；Text/Formula/Steps/Annotations/Regions 其余逐字不变）、`AssistantReplace`、`AssistantRegion`、`AiClient` 请求体 `region`、`ShellController` 圈选接线（`RegionSelectionEnabled`，`ShellController.cs:2163,2237`） | `LocalCorrectionFlowTests` 14 项（`Formula_MiddleSelection_ReplacesOnlySelectedRange`、`Formula_ReplacementWithDifferentLength_KeepsPrefixAndSuffixVerbatim`、`RegionFullyLeftOfAnchor_LeavesContentUnchanged`、`Apply_ReturnsNewSession_LeavesOriginalUntouched`、`Apply_PreservesMetadataAndCollections`、`RepeatedApply_AccumulatesChangesAndPreservesRest` 等）；`RegionTextMapperTests` 12 项；`AiOptimizationTests.Replace_*` 等既有项仍通过。**UI 圈选实测日志链**：`AI 圈选按下：enabled=True anchorEmpty=False` → `AI 缩略图圈选松手：rect=(122.4,33.6)-(364.0,153.6)` → `AI 缩略图圈选：发送 RegionSelected，region=(537.8,302.4)-(1599.3,1382.4)` → `AI 局部纠错入口：...EnableLocalCorrection=True，会话=Done` → `AI 局部纠错结果应用成功：Field=Formula，start=2，length=6，长度 8->10` |
| A2 贴回可编辑 | **已实测通过（双击弹出「编辑 AI 卡片」对话框）** | `Features/AiAssistant/AiPasteMetadata.cs`（camelCase JSON：`kind/latex/text/lines/annotations`）、`BoardImageAsset.Metadata`、`BoardService` Board v3、`ShellController.EditAiCardAsync` | 元数据/Board v3 单测 8 项；实测：贴回后双击（UIA 坐标 352,156）→ 对话框出现，日志命中 Formula 并进入 `EditAiCardAsync`，手动判定与 `DoubleTapped` 去重正常 |
| A3 求解自检 | 已实现·单测通过；**UI 徽标已实测（mock 公式显示「未验证」徽标）** | `Core/AiAssistant/EquationVerifier.cs`、`AssistantEvent.Confidence/Verified`、`ShellController` 公式验证分支 | `AiOptimizationTests.Verifier_SimpleIdentity_IsVerified`、`Verifier_LinearEquation_IsVerified`、`Verifier_Quadratic_TwoRoots_AreVerified`、`Verifier_Quadratic_WithoutRealRoots_IsUnverified`、`Verifier_GarbageText_IsUnverified`、`Verifier_LowConfidence_IsFlagged`、`ApplyEvent_Formula_SyncsConfidenceAndVerified` |
| A4 流式优化 | 已实现·单测通过；**mock 首字与两阶段去重已实测，真实端点未验证** | `Platform/Ai/AiClient.StartTwoStage`（`InkOnly` → 首 Token 后补 `WithImage`）、`LocalFormulaTranscriber`、`AiConfig.TimeoutForStage` | `AiOptimizationTests.Payload_InkOnly_OmitsImage`、`Payload_WithImage_IncludesImage`、`Payload_Region_IsWrittenWhenProvided`、`Payload_LegacySixFields_ArePresent`、`Config_SelectsTimeoutByStage`、`Transcriber_WithFormula_ReturnsPlainText`、`PerformanceBench.Bench_AiClient_MockFirstTokenLatency`；实测两阶段去重后正文标题/条目各出现 1 次 |
| A5 图形释义矢量层 | 已实现·单测通过 | `Core/AiAssistant/ShapeAnnotationGeometry.cs`（`Normalize` 归一化 0..1 / `Project` 按资产 `Left/Top/Width/Height` 投影）、`BoardImageLayer.BuildShapeAnnotationLayer`（Win2D `CanvasControl` 独立矢量折线层） | 随资产移动/缩放重新投影，删除资产即删除；**不写入墨迹 Document、不与墨迹合并**；`ShapeAnnotationGeometryTests` 7 项（`Normalize_UnitPoints_ArePreserved`、`Normalize_PixelPoints_ScalesByAssetSize`、`Normalize_OutOfRangePoints_AreClampedToUnitSquare`、`Project_OffsetsNormalizedPointsByAssetPosition`、`Project_AfterAssetMove_PointsFollowNewLeftTop`、`ShapeMetadata_RoundTripsAnnotationPolylines` 等） |

### 1.2 B 组：课件联动（审计 2.2）

| 编号 | 状态 | 实现文件 / API | 关键单测 |
|---|---|---|---|
| P1 slideId 锚定 + 课堂子任务 | 已实现·单测通过；**P1 完成 3/5：点名贴白板、倒计时结束信号书签、会话列表上限 20（未做：两次框选合并、快捷键表自定义）**。其中会话列表上限 / 留存相关 `ClassroomSessionStore` 与 `SessionRetentionTests` 已随本体删除，书签信号改由插件接收 | `PptInkSession`（`SlideId` 恢复）、`PptInkFileStore`（manifest `LastSlideId`）、`PptModels`；`ShellController.PasteRosterNameAsync`（点名结果渲染 PNG 贴当前白板页，画板未开先打开）、`ShellController.CaptureTimerEndedAsync`（归零发 `TimerEnded` 信号）；~~`ClassroomSessionStore.MaxSessionsLoaded`~~（已删除） | `SlideMigrationTests.ResumeIndex_PrefersSlideIdOverPageNumber`、`SaveAndLoad_RoundTripLastSlideId`、`ManifestWithoutLastSlideId_LoadsVersion2AndKeepsIndexFallback`；`AutoBookmarks_PptRosterAndTimerEnded`；~~`SessionRetentionTests.*`~~（已删除） |
| P2 迁移与恢复向导 | 已实现·单测通过；**向导 UI 未实机** | `SlideInkMigrator`（manifest v2、`.bak-<timestamp>` 备份）、`SlideMappingInspector`、`ShellController.ShowInkRecoveryWizardAsync` | `SlideMigrationTests.LegacyManifestWithoutVersion_MigratesToVersion2AndMapsSlideIds`、`Migration_IsIdempotent`、`Migration_KeepsLegacyIsfFilesAndWritesBackup`、`IndexNamedFilesWithoutManifest_AreMigratedByIndex`、`MissingSlides_AreSkippedAndOldFilesSurvive`、`Inspector_DetectsCountAndOrderChanges`、`Inspector_MissingRatioAboveThirtyPercent_NeedsWarning`、`Migration_WithLargeMissingRatio_ReportsSkippedWithoutClearing` |
| P3 邻页预热/过期丢弃 | 已实现·单测通过；**预取线程实机未验证** | `SlideInkCache`（LRU 8、深拷贝）、`PptInkSession.PrefetchNeighbors`、`SlideSwitchGate`（session/generation） | `BatchBTests.Cache_GetMiss_ReturnsNull_AndCounts`、`Cache_SetThenGet_ReturnsDeepCopy`、`Cache_LruEvictsLeastRecentlyUsed`、`Cache_Capacity_IsRespected`、`Cache_ReSetSameSlide_UpdatesWithoutGrowing`、`Cache_Clear_RemovesEntries`、`Cache_ConcurrentWrites_DoNotThrow`、`PerformanceBench.Bench_SlideInkCache_HitVsMiss_100Iterations` |
| P4 降级矩阵显式化 | 已实现·单测通过；**真实三级降级实机未验证** | `PresentationSources.cs`（`PresentationSourceSelector`：Com → Window → Keys → None） | `BatchBTests.Selector_PrefersComWindowKeys`、`Selector_ReportFailure_DegradesOneStep`、`Selector_Degraded_DoesNotPromoteBackToCom`、`Selector_AllUnavailable_FallsBackToKeys`、`Selector_Reset_RestoresCom`、`Selector_NeverThrows` |
| P5 翻页笔/黑屏 | 已实现·单测通过；**黑屏 F9/Esc 已实测通过；翻页键未实机** | `PresentationKeyMap`、`BlackScreenWindow`、`ShellController.ToggleBlackScreen`、`OverlayHost` 热键注册 | `BatchBTests.KeyMap_NextKeys`、`KeyMap_PreviousKeys`、`KeyMap_B_TogglesBlackScreen`、`KeyMap_UnknownKey_IsNone`；实测 F9 打开「黑屏」全屏窗口、Esc 关闭成功（修复后复测：after F9 列表含“黑屏”，after Esc 不含），点击关闭亦保留 |

### 1.3 C 组：时光机（审计 2.3）〔历史归档：本体实现；插件化后仅时间轴 / 书签 / 导出纯视频结论沿用，音频与恢复条目关闭〕

| 编号 | 状态 | 实现文件 / API | 关键单测 / 实测 |
|---|---|---|---|
| T0 入口 | 已实测通过 | `TimeMachineWindow` 入口判定 | 无录制时显示通知「暂无录制记录。先在画板中书写，再打开时光机。」（非独立窗口，属正常设计）；有录制时打开回放窗口 |
| T1 统一时间轴 | 已实现·单测通过 | `TimelineEvent`（Ink/Ppt/Roster/Timer/Bookmark）、`InkTimeline.SceneAt`、`InkRecorder.Append` | `TimeAxisTests.RecorderAppend_MergesCrossSourceEventsInTimeOrder`、`RecorderAppend_WhenNotRecording_IsIgnored`、`RecorderAppend_ConcurrentDuringRecording_DoesNotThrow`、`SceneAt_RestoresPageAndBackgroundAtAnyTime`、`SceneAt_RestoresPptSlideRosterAndTimer`、`SceneAt_NoEvents_UsesDefaults` |
| T2 自动书签 | 已实现·单测通过 | `InkTimeline.AutoBookmarks/ApplyAutoBookmarks`、`SessionBookmark` | `TimeAxisTests.AutoBookmarks_PageAndClearNamedByPageNumber`、`AutoBookmarks_PptRosterAndTimerEnded`、`AutoBookmarks_MergesEventsWithinTwoSeconds`、`AutoBookmarks_KeepsEventsTwoSecondsOrMoreApart`、`AutoBookmarks_ApplyWritesIntoSession_AndAddBookmarkIsSorted`、`Bookmarks_RenameAndRemove`、`Bookmarks_RenamedAutoBookmark_IsNotOverwrittenByAutoRules`、`SessionV2_SaveLoad_RoundTripsBookmarks` |
| T3 长课性能 | 已实现·单测通过；**抽稀与回放定位已实测** | `StrokeSimplifier`（RDP，容差 0.8）、`SessionJournal`（JSONL，2s/50 事件 flush）、`InkTimeline` 检查点 | `BatchCTests.Simplify_*`（8 项）、`Journal_AppendAndRecover_ReturnsSameEvents`、`Journal_TruncatedLastLine_RecoversWithoutThrowing`、`Journal_MergesWithSnapshot_AndPrefersLongerEventStream`、`Journal_RecoveredSessionMatchesFullSave`、`TimeMachineTests.Checkpoint_GeneratedByCount_AndNearestIsCorrect`、`BuildFrame_CheckpointIncrementalMatchesExpected`、`PerformanceBench.Bench_StrokeSimplifier_10000Points`、`Bench_InkTimeline_BuildFrameFromCheckpoint` |
| T4 视频导出〔历史归档：视频管线迁至插件；音频列作废〕 | 当时已实现 | `TimeMachineExporter`（Media Foundation `IMFSinkWriter` H.264）、`TimeMachineWindow.RunExportAsync` | `BatchCTests.ExportOptions_ValidateRejectsBadRangesAndPaths`、`ExportOptions_SampleFrameTimes_RespectsFpsAndBounds`；实测合成会话 2 秒/30fps → 60 帧、66,537 字节，文件头 `ftypmp42` 有效；~~带音频合成 48,167 字节，`soun`/`mp4a`~~ 音频链已删除 |
| T5 音频录制与合成 | **〔已随插件化移除〕** WASAPI loopback 录制、WAV 重采样与 MF AAC 音轨合入不迁入插件，插件导出为纯视频；本行历史实测数字（合成音轨 48,167 字节、`soun`/`mp4a`）仅留档 | ~~`TimeMachineAudioCapture`~~（已删除）、~~`TimeMachineExporter` 音轨合并~~ | ~~合成音轨 MP4、无音频设备降级 `HasAudio=false` / `AudioNote`~~ |
| T6 回放还原 PPT 画面 | **已实现·单测通过；真实 PPT 截图回放 UI 未实机** | `PptSlideChanged` payload 携带截图路径；回放窗口 `Image` 背景 + 网格/坐标；`ReplayBackgrounds` | Classroom 全量 164/164 通过（含回放背景渲染与图形释义几何路径）；真实 PPT 截图回放 UI 仍未实机 |

### 1.4 F 组：跨模块工程（审计第 4 节）

| 编号 | 状态 | 实现文件 / API | 验证情况 |
|---|---|---|---|
| F1 Feature Flag | **部分**：`AiSettings` 4 项已实现；`ClassroomSettings` 未新增 | `Core/Settings/AppSettings.cs:AiSettings`、`ShellController` 4 处开关分支 | 无专门单测；默认值/回退分支见第 2 节 |
| F2 数据版本 | 已实现·单测通过（历史记录） | Board v3（`BoardService.BoardFormatVersion`）、Ppt manifest v2（`PptInkFileStore.CurrentManifestVersion`）；~~Session v2（`ClassroomSession.Version`）、journal/snapshot~~ 已随本体删除，插件课堂录制改 `.icink` ZIP | 见 A2/P2/T3 单测；本次复跑通过 |
| F3 测试基线 | **部分（欠账登记）** | 既有 `tests/InkCanvas.Core.Tests` 仍不可编译；独立项目已覆盖 | 本次复核 `dotnet build tests/InkCanvas.Core.Tests` 失败；Ai 79/79、Classroom 164/164 通过 |
| F4 性能度量 | **部分（合成/mock 已实测）** | `tests/InkCanvas.Classroom.Tests/PerformanceBench.cs` | 本次 5 个基准测试全通过，输出 8 条 `[PERF]`；真实端点/真实渲染未验证 |

---

## 2. Feature Flag

### 2.1 AiSettings（`src/InkCanvas.Core/Settings/AppSettings.cs:42`）

| JSON 键 | 属性 | 默认 | 关闭后的回退行为 | 接线位置 |
|---|---|---|---|---|
| `enableLocalCorrection` | `EnableLocalCorrection` | `true` | 关闭圈选入口（`RegionSelectionEnabled=false`），局部纠错分支直接返回，不替换正文 | `ShellController.cs:1439,1884` |
| `enableEditablePaste` | `EnableEditablePaste` | `true` | 贴回退回旧行为：仅生成 PNG（`image/png`），不写可编辑元数据 | `ShellController.cs:1825` |
| `enableVerification` | `EnableVerification` | `true` | 不做本地公式验证，`Confidence/Verified` 不参与展示 | `ShellController.cs:1553,1748` |
| `enableTwoStage` | `EnableTwoStage` | `true` | 退回单段请求（截图 + 墨迹一次发送），不启用 `InkOnly → WithImage` 两阶段 | `ShellController.cs:1494` |

- 四项均默认 `true`，旧版 `Settings.json` 缺少键时按默认值升级，无需迁移。
- **未实现**：审计第 4 节提及的 `ClassroomSettings` 分组未新增；现有课堂/PPT 开关仍沿用既有 `AppSettings` / `PowerPointSettings` 字段。
- 未启动 App 验证设置页交互，仅核对代码分支与默认值。

### 2.2 关闭 Flag 的语义边界

关闭开关不会删除已有数据：可编辑贴回的历史元数据仍保留在 Board 文件中；关闭后新贴回为纯 PNG。关闭两阶段不影响已建立的会话，仅影响下一次请求。

---

## 3. 数据格式与迁移

### 3.1 Board v3

- 格式标识：`inkcanvas-board`；版本常量 `BoardService.BoardFormatVersion = 3`（`src/InkCanvas.Features/Board/BoardService.cs:57`）。
- 新增：每张图片的 `Metadata`（JSON 字符串，camelCase）。`AiPasteMetadata` 版本为 1，字段：`version/kind/latex/text/lines/annotations`。
- 兼容：加载时 `Version < 1` 或 `> 3` 拒绝；v1/v2 文件缺少图片元数据时按空值处理，页面元数据缺失时回退默认值。
- 单测：`SaveLoadV3_PreservesImageMetadata`、`LoadLegacyV2Board_ImageMetadataDefaultsToEmpty`、`UpdateImage_UndoRestoresPreviousContentAndRedoReappliesIt`。
- **回滚方法**：v3 文件含 `Version: 3`，旧客户端会按“不支持版本”拒绝。回滚前必须保留 v2 原文件或备份；不建议手改 `Version` 字段降级，因为 v3 可能含旧客户端不认识的图片 `Metadata`（旧客户端会忽略，但无法保证往返）。

### 3.2 PPT manifest v2 与 `.bak`

- 版本常量：`PptInkFileStore.CurrentManifestVersion = 2`；清单文件名 `InkCanvas-Ppt.json`。
- 新增：`Version`、`LastSlideId`、按 SlideId 命名的 `slide-{SlideId}.icink`；仍写 `Position` 作为旧版页码降级字段。
- 迁移：`SlideInkMigrator.MigrateIfNeeded` 把旧按页码/索引命名的文件映射为 SlideId；迁移前 `SlideInkMigrator.Backup` 生成 `<目录>.bak-yyyyMMdd-HHmmss`（重名自动加后缀）。
- 旧 WPF ISF（`.icstk/.icart`）**只检测与保留，不转换、不删除**；迁移合同禁止删除旧数据。
- 单测：`LegacyManifestWithoutVersion_MigratesToVersion2AndMapsSlideIds`、`Migration_KeepsLegacyIsfFilesAndWritesBackup`、`Migration_IsIdempotent`、`IndexNamedFilesWithoutManifest_AreMigratedByIndex`。
- **回滚方法**：停止 App → 用 `.bak-*` 目录整体替换当前课件墨迹目录 → 恢复旧客户端可读的按页码布局。若旧客户端仍读 manifest，它会忽略未知字段；但若新版本已删除旧位置文件，必须使用备份目录恢复。

### 3.3 Session v2〔历史归档：旧录制会话 JSON 格式已删除；插件课堂录制改 `.icink` ZIP（`inkcanvas-classroom` v1）〕

- `ClassroomSession.Version` 默认 `2`；v1 无 `Version/Bookmarks` 时按默认值兼容。
- 新增 `Bookmarks`（`timeMs/pageIndex/name/auto/kind`）；JSON 使用 camelCase 键名。
- 单测：`SessionV2_SaveLoad_RoundTripsBookmarks`、`LegacyV1Session_WithoutVersionOrBookmarks_Loads`、`SessionV2_JsonUsesCamelCaseKeys`、`Session_SaveLoad_RoundTrips`。
- **回滚**：v2 文件交给不支持书签的旧客户端时，未知字段被忽略，事件流仍可回放；但旧客户端保存后会丢失书签。回滚前保留原 JSON。

### 3.4 journal / 快照〔历史归档：增量 journal 与快照恢复链路已删除，不迁入插件〕

- 路径：`%APPDATA%\InkCanvas\sessions\<id>.journal`（JSONL）；快照 `<id>.snapshot.json`。
- 追加写：每 2 秒或 50 个事件 flush；正常停止时合并为正式会话 JSON 并删除恢复产物；异常退出最多丢约 2 秒。
- 恢复：`ClassroomSessionStore.LoadWithJournal` 合并最后一个完整快照与日志；截断的最后一行直接忽略。
- 单测：`Journal_AppendAndRecover_ReturnsSameEvents`、`Journal_TruncatedLastLine_RecoversWithoutThrowing`、`Journal_MergesWithSnapshot_AndPrefersLongerEventStream`、`Journal_RecoveredSessionMatchesFullSave`、`Journal_OldSessionWithoutJournal_LoadsNormally`。
- **未实现**：App 启动时检测 `HasPending/PendingSessionIds` 并弹窗恢复的 UI 未接线（`src/InkCanvas.App` 无调用点）。
- **回滚**：journal 与快照均为增量恢复文件，删除即可退回“只读正式会话 JSON”；正式 JSON 在合并前不被覆盖。

---

## 4. 性能对比

### 4.1 基准原始输出

执行命令：

```
dotnet test tests\InkCanvas.Classroom.Tests --filter FullyQualifiedName~PerformanceBench --logger "console;verbosity=detailed"
```

真实输出（本次最终运行）：

```
[PERF] name=timeline.buildFrame.seek.avg ms=131.021 detail=events=50500;strokes=50000;durationMs=505000;targetMs=404000;checkpoints=252;replayEvents=0;frameStrokes=100;coldMs=664.804;runs=5;minMs=110.814;maxMs=151.445
[PERF] name=timeline.buildFrame.seekOffset.avg ms=84.464 detail=events=50500;targetMs=403766;replayEvents=176;frameStrokes=76;runs=5;minMs=74.597;maxMs=93.942
[PERF] name=simplifier.points10k.avg ms=3.641 detail=points=10000;tolerance=0.8;runs=5;kept=1179;retentionPct=11.79;minMs=3.606;maxMs=3.701;wholeStrokeMs=3.913;wholeKept=1179
[PERF] name=memory.cache1000Strokes ms=2.130 detail=strokes=1000;pointsPerStroke=16;managedDeltaBytes=1052008;managedDeltaKB=1027.4;bytesPerStroke=1052.0;workingSetDeltaKB=-320.0;note=含源笔画与缓存深拷贝，GC.GetTotalMemory(true) 前后差
[PERF] name=ai.mock.firstToken.avg ms=63.235 detail=endpoint=mock;mode=Formula;runs=3;samplesMs=67.4/60.7/61.6;expectedFirstDelayMs=60;note=不含真实网络/模型推理
[PERF] name=slidecache.set.avg ms=0.181 detail=strokes=1000;pointsPerStroke=12;iterations=100;totalMs=18.126
[PERF] name=slidecache.getHit.avg ms=0.135 detail=strokes=1000;pointsPerStroke=12;iterations=100;totalMs=13.454;hits=101;deepCopy=true
[PERF] name=slidecache.getMiss.avg ms=0.001 detail=strokes=1000;pointsPerStroke=12;iterations=100;totalMs=0.112;misses=100
```

测试结果：性能基准 **5/5 通过**，总耗时约 3.7 秒。

### 4.2 对比表

统一测试条件：本机 Windows 11 Home China、.NET 10.0.404、Debug、x64、xunit 2.9.2；单进程、无 UI 渲染、无真实磁盘/网络 I/O；除 `timeline.cold` 外均已 JIT 预热；每项取多次运行平均。**基准为合成负载，不等于真实课堂设备表现。**

| 指标 | 优化前（基线或估算） | 优化后实测 | 测试条件 / 说明 |
|---|---|---|---|
| 切页缓存命中 `Get`（1000 笔画 × 12 点，深拷贝） | 无缓存时每次需从会话/磁盘同步加载；**未单独实测** | **0.135 ms/次**（100 次总 13.454 ms） | `SlideInkCache` 容量 8；命中返回深拷贝，调用方修改不污染缓存 |
| 切页缓存未命中 `Get` | — | **0.001 ms/次**（100 次总 0.112 ms） | 只查字典并计数；实际未命中后的加载由 session/磁盘承担，未计入 |
| 切页缓存写入 `Set`（深拷贝 1000 笔画） | 无缓存写入 | **0.181 ms/次**（100 次总 18.126 ms） | 含源笔画一次性构造后的重复深拷贝 |
| 回放定位（对齐 0.8 目标，命中检查点边界） | 无检查点时从头重放 50,500 事件；**未单独实测** | **131.021 ms**（5 次平均，min 110.814 / max 151.445） | 50,000 笔画、50,500 事件、252 个检查点；`replayEvents=0`、`frameStrokes=100`；冷路径（首次 registry 反序列化 + 检查点生成）**664.804 ms** |
| 回放定位（检查点后增量重放） | 无检查点时需全量重放 | **84.464 ms**（5 次平均，min 74.597 / max 93.942） | 目标 403766 ms，距最近检查点重放 **176** 个事件；帧内 76 笔画 |
| 笔画抽稀（10,000 点，容差 0.8） | 完整保留 10,000 点（100%） | **3.641 ms**，保留 **1,179 点（11.79%）** | 正弦 + 高频抖动合成曲线；整笔 `Simplify` 3.913 ms，保留点数相同 |
| AI 首字（mock） | 单段无基线；真实端点**未配置/未验证** | **63.235 ms**（3 次：67.4 / 60.7 / 61.6 ms） | 端点为空触发 `MockStream`，常量 `FirstDelayMs=60`；仅验证调度，不含网络/模型/截图编码 |
| 1000 笔画缓存内存 | 无基线 | **1,052,008 bytes（1,027.4 KB，约 1,052 B/笔）** | `GC.GetTotalMemory(true)` 前后差，含源笔画与缓存深拷贝；`Process.WorkingSet64` 差为 -320 KB（噪声，不作为结论） |

### 4.3 性能观察（不作为已修复项）

- 回放定位的主要耗时**不在检查点重放本身**：对齐点 `replayEvents=0` 仍需 131 ms；非对齐点重放 176 个事件却为 84 ms。两者帧内笔画数不同（100 vs 76），符合 `BuildFrame` 中 `OrderOf` 对每个结果笔画线性扫描整个 registry（`O(frame × registry)`）以及 `PageAt` 线性扫描事件的特征。后续若要把 90 分钟长课回放压到 16 ms 级，应先为笔画顺序/页码建立索引，而不是继续加密检查点。
- 冷路径 664.804 ms 包含 50,000 次 `StrokeJsonCodec.Deserialize` 与 252 个检查点生成，属于首次打开长会话的一次性成本；本次未做优化。
- `SlideInkCache` 命中成本随笔画点数线性增长，深拷贝是正确性（线程隔离）的代价；0.135 ms 对 1000 笔画/页的切页可接受，但 5000 笔以上页面需关注。

### 4.4 mock 与真实的差异（必须区分）

| 维度 | 本次实测（mock/合成） | 真实环境（未验证） |
|---|---|---|
| AI 首字 | 63 ms = `Task.Delay(60)` + 调度 | 需经 `AI_ENDPOINT`；含网络、排队、模型推理、截图编码上传；可能数百 ms 至数秒 |
| 两阶段收益 | 未测量真实 `InkOnly` 首字 vs `WithImage` 首字 | 端点未配置，无法验证“墨迹先行”的实际收益 |
| 切页/回放 | 纯内存深拷贝/事件重放 | 真实 Win2D 渲染、GPU 合成、磁盘 I/O、UI 线程争用未计入 |
| 抽稀 | 合成正弦曲线 | 真实手写采样密度/抖动不同，保留率会变化 |
| 内存 | 单进程 managed 堆差 | 真实 App 含 WinUI/Win2D 原生内存、GPU 资源，数值不可直接外推 |

### 4.5 连续翻页压力实测（新增，含切页耗时埋点）

在真实 App 会话中连续翻页 60 次，开启切页耗时埋点，观察页码联动、长任务与进程存活：

| 项目 | 实测值 | 说明 |
|---|---|---|
| 总耗时 | **30,465 ms / 60 次**（约 2 页/秒） | 自动化连续翻页节奏 |
| 页码采样 | 10:1、20:2、30:3、40:1、50:2、60:3 | 每 10 次采样一次 PowerPoint → App 页码联动 |
| 失配 | **0** | 采样点未出现页码错位 |
| 切页 >50 ms 长任务 | **0 次**（`over50 count=0`） | 切页耗时埋点统计，未出现超阈值长任务 |
| 日志 Error | **0**（17 点日志） | RPC 忙已降级为 Trace + 10 秒节流 |
| 进程存活 | InkCanvas 进程存活 | 全程未崩溃 |
| 异常 | PowerPoint 忙 RPC（`0x80010001`）已降级 | 降级为 `Trace` + 10 秒节流，未影响翻页流程、未崩溃 |
| 未量化 | **残影 / 闪烁** | 本轮无批注墨迹、无 ETW 采样，无帧级客观量化；切页长任务已量化 0 次，但残影不作性能达标证据 |

> 压力脚本的逐次日志未归档到 `docs/`，上表数据以本次会话观测为准；如需复核，应重跑翻页脚本并留存日志。

---

## 5. 回归与兼容矩阵

### 5.1 本次实际执行

| 项目 | 命令 | 结果 |
|---|---|---|
| 性能基准 | `dotnet test tests\InkCanvas.Classroom.Tests --filter FullyQualifiedName~PerformanceBench --logger "console;verbosity=detailed"` | **5/5 通过**，8 条 `[PERF]` 输出 |
| Classroom 全量 | `dotnet test tests\InkCanvas.Classroom.Tests` | **164/164 通过**（含 `ShapeAnnotationGeometryTests` 7 项、`SessionRetentionTests` 5 项、`PerformanceBench` 5 项） |
| AI 全量 | `dotnet test tests\InkCanvas.Ai.Tests` | **79/79 通过**（含 `LocalCorrectionFlowTests` 14 项、`RegionTextMapperTests` 12 项） |
| App 构建 | `dotnet build src/InkCanvas.App/InkCanvas.App.csproj -c Debug -p:Platform=x64` | **0 错误、0 警告** |
| 连续翻页压力 | 真实 App 会话连续翻页 60 次（含切页耗时埋点） | **60 次 / 30,465 ms，页码失配 0，切页 >50 ms 长任务 0 次（`over50 count=0`），17 点日志 Error 0，进程存活**；残影/闪烁仍未量化 |
| 既有基线 | `dotnet build tests/InkCanvas.Core.Tests` | **失败**（见 5.3） |

### 5.2 兼容矩阵

| 场景 | 状态 | 依据 |
|---|---|---|
| Office 2021 放映进入/退出 | 已实测通过（P0 早期联调） | `docs/ppt-wps-p0.md`：真实 PowerPoint COM 联调通过 |
| Office 2021 COM 翻页 / 导航器点击翻页 | 已实测通过（P0 早期联调） | 同上：外部翻页后 UIA 页码联动、导航器点击 2→3 通过 |
| Office 2021 按页批注隔离与恢复 | 已实测通过（P0 早期联调） | 同上：第 1 页墨迹 → 第 2 页无残影 → 返回恢复 |
| WPS（`KWPP.Application`）真实放映 | **未验证（未联调）** | 仅验证 ProgID 可注册，未启动 WPS 放映 |
| 多屏 / 不同 DPI | **未验证** | 需多显示器实机 |
| 三级降级矩阵 | 单测通过 | `BatchBTests.Selector_*` 6 项 |
| 黑屏 / 翻页笔 | **黑屏已实测通过（F9 开、Esc 关、点击关闭）**；翻页键单测通过 | 实机复测：after F9 列表含“黑屏”，after Esc 不含；`BatchBTests.KeyMap_*` 4 项 |
| 时光机入口〔历史归档〕 | 当时已实测通过 | 无录制显示「暂无录制记录。先在画板中书写，再打开时光机。」；有录制打开回放窗口。插件化后入口为插件命令「开始时光机录制 / 停止录制」「时光机录制」 |
| 双击编辑 AI 贴回 | **已实测通过** | 贴回 AI 卡片后双击（UIA 坐标 352,156）→ 弹出「编辑 AI 卡片」对话框；日志命中 Formula 并进入 `EditAiCardAsync`，手动判定与 `DoubleTapped` 去重正常 |
| 局部圈选纠错 | **已端到端实测成功（圈选 → RegionSelected → LocalCorrectionFlow 定点替换）** | 实测日志链：`AI 圈选按下：enabled=True anchorEmpty=False` → `松手：rect=(122.4,33.6)-(364.0,153.6)` → `发送 RegionSelected，region=(537.8,302.4)-(1599.3,1382.4)` → `EnableLocalCorrection=True，会话=Done` → `结果应用成功：Field=Formula，start=2，length=6，长度 8->10`；另 `LocalCorrectionFlowTests` 14 项 + `RegionTextMapperTests` 12 项保证其余 Formula/Text/Steps/Annotations 逐字不变 |
| 图形释义矢量层 | 已实现·单测通过 | `ShapeAnnotationGeometryTests` 7 项；Win2D `CanvasControl` 独立矢量折线层，随资产移动/缩放，删除资产即删除，不写入墨迹 Document |
| 连续翻页压力 | **已实测（60 次 / 30,465 ms，页码失配 0，切页 >50 ms 长任务 0，Error 0，进程存活）** | 采样 10:1、20:2、30:3、40:1、50:2、60:3；`PPT slide apply >50ms` 计数 0（`over50 count=0`）；17 点日志 Error 0；RPC 忙已降级为 Trace + 10 秒节流；**残影/闪烁仍未量化** |
| 回放还原 PPT 画面 | 已实现·单测通过 | `PptSlideChanged` payload 含截图路径；回放 `Image` 背景 + 网格/坐标；真实 PPT 截图回放 UI 未实机 |
| 恢复向导 / journal 启动弹窗〔已随本体移除〕 | 向导代码存在但**未实机**；启动弹窗**未实现** | ~~`ShellController.ShowInkRecoveryWizardAsync`~~；~~`SessionJournal.HasPending`~~ 均已删除，异常恢复提示不迁入插件 |
| MP4 导出〔历史归档：视频管线迁至插件；音频链已移除〕 | 当时已实测通过 | 纯视频：2 秒/30fps → 60 帧、66,537 字节，`ftypmp42` 有效；~~含合成 AAC 音轨：48,167 字节、`soun`/`mp4a`；`HasAudio=false` 降级~~ 音频部分作废，插件导出为纯视频 |

### 5.3 既有基线欠账

`tests/InkCanvas.Core.Tests` 仍不可编译，本次复核错误示例（未修改该测试）：

- `VectorParityTests.cs`：`InkController` 缺少 `ToolChanged`、`EraserMode`、`CloneMode`、`StickyShape`、`SelectionFrame`、`OnPointerCanceled`、`ClearSelection`、`SelectAll` 等成员（CS1061）。
- `InkEraserMode`、`StrokeEraser` 类型不存在（CS0246/CS0103）。
- `OverlayPointer` 构造函数参数数量不匹配（CS1729）。
- `ShapeGenerator` 缺少 `GenerateCuboidDepth`、`GenerateHyperbolaBranches`、`SnapParallelEnd`（CS0117）。

这与本需求无关，按既有口径登记为“新增/可运行测试全过 + 基线欠账单独登记”；本次未获授权补实现缺失 API。

---

## 6. 未实现 / 降级项

> 原列于本节的“MP4 实机编码”“双击编辑 UI”“局部圈选 UI”已实机复测通过并移出本节：MP4（2 秒/30fps → 60 帧、66,537 字节，`ftypmp42` 有效）；双击贴回卡片弹出「编辑 AI 卡片」对话框（日志命中 Formula、进入 `EditAiCardAsync`）；局部圈选端到端成功（圈选 → RegionSelected → 定点替换，日志链见 A1/5.2）。本轮“局部纠错替换策略（v1 降级）”已由定点替换实现替代、“图形释义未实现”已由独立矢量图层补齐，两条均已移出本节；新增“连续翻页的残影/闪烁量化”“90 分钟长课”作为未量化项保留。~~音频已实现录制与合成~~ 讲解音频链路已随插件化移除，不再保留为未验证项。

| 项目 | 现状 | 影响 / 说明 |
|---|---|---|
| 真实 PPT/WPS 截图回放 UI | `PptSlideChanged` 已带截图路径，回放 `Image` 背景 + 网格/坐标已实现；真实 PPT/WPS 截图与回放 UI 未实机 | 回放画面依赖真实截图产物，合成/单测不能替代真实放映抓图 |
| 音频真实系统声完整导出〔已随本体移除〕 | ~~WASAPI loopback 录制 + MF AAC 合并~~ 已删除，插件导出为纯视频，无音轨 | 该行历史实测数字（48,167 字节、`soun`/`mp4a`、`HasAudio=false`）仅留档，不作为现行能力 |
| 连续翻页的残影/闪烁 | 60 次翻页压力已实测（30,465 ms、失配 0、切页 >50 ms 长任务 0 次、Error 0）；**未采集批注墨迹、未开 ETW** | 切页长任务已量化 0 次；残影/闪烁无帧级客观量化，需带批注 + ETW 的专项压测，不作为性能达标证据 |
| 90 分钟长课〔历史归档：压测归属插件〕 | 未实测 | 当时 journal/检查点/抽稀为单测与合成基准；90 分钟真实课堂未跑通；长课压测后续归「时光机」插件测试 |
| ~~journal 启动弹窗~~〔已随本体移除〕 | ~~`SessionJournal.HasPending/PendingSessionIds`~~ 已删除 | 异常退出恢复链路不迁入插件 |
| 缩略图 | `BoardService.GetPageThumbnail` 返回计数与名称 | 非位图缩略图，不能直接作为页面预览贴图 |
| 放大镜 | 降级为截图方案 | 非实时矢量放大；未实机验证效果 |
| 回放帧级〔历史归档：时间轴帧渲染归插件参考〕 | ~~`InkTimeline.BuildFrame`~~ 整笔增删 | 无笔内插值/逐点显现，快速书写回放可能“整笔跳出” |
| `ClassroomSettings` | 未新增 | F1 仅完成 `AiSettings` 4 项；课堂侧仍用既有设置字段 |
| P1 剩余两项 | 两次框选合并、快捷键表自定义未做 | P1 历史 3/5：点名贴白板、倒计时结束信号书签已完成；~~会话列表上限 20~~ 已随本体删除 |
| 真实 AI 两阶段收益 | 端点未配置 | mock 首字 63 ms 不能代表真实首字 |
| WPS / 多屏 / DPI | 未联调/未验证 | 不声称兼容 |


---

## 7. 验收结论

按需求 7.2 口径（新增/可运行测试全过 + App 构建 0 错误）：**已达成**。

- **测试与构建**：AI 全量 **79/79**、Classroom 全量 **164/164**、`PerformanceBench` 5/5；`InkCanvas.App` Debug x64 构建 **0 错误、0 警告**。
- **本轮新增且已验证**：
  - **局部纠错定点替换与圈选**：`RegionTextMapper`（rect 比例 → 字符区间）+ `LocalCorrectionFlow.Apply`（克隆会话、仅替换目标区间，Text/Formula/Steps/Annotations/Regions 其余逐字不变）；`LocalCorrectionFlowTests` 14 项端到端逻辑测试、`RegionTextMapperTests` 12 项。**UI 圈选已端到端实测成功**：日志链 `AI 圈选按下：enabled=True anchorEmpty=False` → `松手：rect=(122.4,33.6)-(364.0,153.6)` → `发送 RegionSelected，region=(537.8,302.4)-(1599.3,1382.4)` → `EnableLocalCorrection=True，会话=Done` → `结果应用成功：Field=Formula，start=2，length=6，长度 8->10`。
  - **图形释义独立矢量图层**：`ShapeAnnotationGeometry.Normalize/Project` + `BoardImageLayer` 的 Win2D 矢量折线层，随资产移动/缩放重新投影、删除资产即删除，**不写入墨迹 Document**；`ShapeAnnotationGeometryTests` 7 项。
  - **连续翻页压力（含切页耗时埋点）**：60 次 / **30,465 ms**（约 2 页/秒），页码采样 10:1、20:2、30:3、40:1、50:2、60:3，**失配 0**；`PPT slide apply >50ms` 长任务计数 **0**（`over50 count=0`），17 点日志 **Error 0**；RPC 忙已降级为 Trace + 10 秒节流，进程存活未崩溃。**残影/闪烁仍未量化**（无批注墨迹、无 ETW），不作为性能达标证据。
  - **P1 子任务 3/5（历史记录）**：点名贴白板、倒计时结束信号书签已完成；~~会话列表上限 20（`LoadAll/Prune/Delete`）~~ 随本体删除；两次框选合并、快捷键表自定义未做。
- **既有已实测通过（历史记录）**：双击编辑 AI 卡片（UIA 352,156 → 对话框）；MP4 纯视频 60 帧 / 66,537 字节（视频管线后迁至插件）；~~含合成 AAC 48,167 字节（`soun`/`mp4a`）~~ 音频链已删除；黑屏 F9 开 / Esc 关；~~时光机入口~~ 插件化后改为插件命令；Office 2021 放映 / 翻页 / 按页批注（P0 早期联调）。
- **性能数据**（mock/合成基准，真实环境未复现）：切页缓存命中 0.135 ms/次、未命中 0.001 ms/次；回放定位 131.021 ms（冷 664.804 ms）；抽稀 3.641 ms、保留率 11.79%；mock 首字 63.235 ms；1000 笔画缓存约 1,027.4 KB。
- **仍未验证（历史记录，不计入“已通过”）**：真实 AI 端点与两阶段收益、WPS 放映、多屏 / 不同 DPI、真实 PPT 截图回放 UI、带批注/ETW 的残影/闪烁量化、P1 剩余两项（两次框选合并、快捷键表自定义）。~~真实系统声完整导出、90 分钟长课、journal 启动弹窗~~ 对应音频 / 长课压测 / 恢复链路已移除或归插件测试，不再作为本体未验证项。

**结论：单测、构建与上述已实测项满足 7.2 验收口径；剩余仅真实环境（真实 AI/WPS、多屏/DPI、真实截图回放 UI、真实系统声、90 分钟长课）、残影/闪烁量化、P1 两项（框选合并、快捷键表自定义）仍为“未验证/未做”，不声称端到端全部通过。**