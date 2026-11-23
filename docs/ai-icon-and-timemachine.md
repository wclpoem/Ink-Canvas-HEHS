# AI 图标替换与板书时光机完善交付记录

> **【归档说明 · 2026-11-20 记录】** 本文含两条主线：**§2、§8 的 AI 图标部分为现行有效记录**；其余时光机相关章节为历史归档——时光机已重构为独立插件模块「时光机」（Id `inkcanvas.time-machine`），录制暂停 / 继续、异常会话恢复、讲解音频、`.sha256` 校验与 `.bak` 迁移等本体链路已移除，不迁入插件。历史章节中的能力、用例与计数均不代表当前形态，现行插件说明见 `docs/timemachine-user-guide.md`。

> 本文为 2026-11-20 的上一轮记录。2026-11-21 的可靠性升级（事件序号、完整性校验、分块加载、主题修复、验收证据）见 `docs/timemachine-reliability-v2.md` 与 `docs/timemachine-test-evidence.md`（均为同期历史归档）。

- 目标平台：Windows 桌面端（WinUI 3 / Windows App SDK）
- 技术栈：C# / .NET 10，渲染沿用 Win2D，未引入 Electron / Web 技术栈
- 变更性质：增量优化，不改既有数据格式语义、不重写交互、不回退其他语言实现
- 记录日期：2026-11-20

## 1. 变更概览

本次交付包含两条相对独立的主线：

1. **AI 图标替换（仅 WinUI）**：以文本标记 `Glyphs.Ai = "AI"` 取代原先易与帮助入口混淆的问号图标，覆盖主浮条、次级菜单、课堂工具箱与 AI 助教浮窗头部；「操作指南」仍保留问号图标，形成明确区分。
2. **板书时光机完善（历史归档）**：本节描述的录制暂停与继续、视图缩放平移记录、事件去重与乱序插入、异常会话恢复提示、会话元信息展示、回放逐事件定位与「仅板书 / 完整课堂」切换、手动书签、异步定位取消、导出参数与磁盘检查等，均为本体阶段的历史能力；插件化后以 `docs/timemachine-user-guide.md` 为准。

整体保持既有 Session / Board / PPT 数据结构可用，新增字段均向后兼容旧数据。

## 2. AI 图标替换范围清单

统一约定：`Glyphs.Ai = "AI"` 是**哨兵标记**，不是字体码位。所有渲染层在检测到该值时输出文本「AI」，其余值仍按 Segoe Fluent Icons 码位渲染。

| 文件 | 位置 | 旧 | 新 | 备注 |
| --- | --- | --- | --- | --- |
| `src/InkCanvas.Controls/Controls/Glyphs.cs` | 图标常量 | 无 | `public const string Ai = "AI";` | 文本哨兵，避免与真实码位冲突 |
| `src/InkCanvas.Controls/Controls/TouchIconButton.cs` | `UpdateContent()` | 问号类字形 | 文本「AI」 | 12px SemiBold；按钮 40×40、`Padding = 8` 保持不变；选中态前景色随主题切换 |
| `src/InkCanvas.Controls/Controls/FloatingBar.cs` | 主栏「AI 识别（先框选内容）」 | 问号图标 | `Glyphs.Ai` | 点击仍走 `Shell.OpenAiAssistant()` |
| `src/InkCanvas.Controls/Controls/FloatingBar.cs` | 次级菜单「AI 助教」 | 问号图标 | `Glyphs.Ai` | 与主栏同源能力，入口保留两处 |
| `src/InkCanvas.Controls/Controls/FloatingBar.cs` | 无障碍名称 | 仅 tooltip | `AutomationProperties.Name = tooltip` | 屏幕阅读器读作「AI 识别（先框选内容）」「AI 助教」 |
| `src/InkCanvas.App/Views/ToolkitWindow.cs` | `AddTool(...)` 图标分支 | 问号字形 | 24×22 容器内 15px SemiBold 文本「AI」 | 行高与同排 18px 字体图标对齐，不截断 |
| `src/InkCanvas.App/Views/ToolkitWindow.cs` | 工具箱「AI 助教」按钮 | 问号图标 | `Glyphs.Ai` | `AutomationProperties.Name` 设为可见标签 |
| `src/InkCanvas.App/Views/AssistantWindow.cs` | 标题栏头部 | 无徽标 | 标题前 10px SemiBold「AI」徽标 | 使用强调色，与「AI 助教」标题形成层级 |
| `src/InkCanvas.Controls/Controls/FloatingBar.cs` | 「操作指南」 | `Glyphs.Help` | 保持不变 | 右侧问号仅代表帮助入口，不再承担 AI 语义 |

验收结论（已实测）：主栏与工具箱显示文本「AI」；右侧问号仅为帮助入口；布局无截断、无错位；代码检索 `Glyphs.Help` 仅剩「操作指南」一处，无 AI 问号残留。

## 3. 时光机能力矩阵〔历史归档：暂停 / 继续、journal 恢复、会话列表、讲解音频等行对应的本体链路已移除；插件仅保留录制、章节书签与纯视频导出〕

| 能力 | 状态 | 说明 |
| --- | --- | --- |
| 笔画 / 擦除 / 清空 / 切页 / 背景事件 | 已有 | 既有时间轴能力，本次回归通过 |
| 撤销重做、PPT 翻页批注 | 已有 | 事件类型与回放路径未改 |
| 点名 / 计时等课堂事件 | 已有 | 沿用既有事件写入 |
| 自动书签、检查点、抽稀 | 已有 | 抽稀策略与检查点粒度未改 |
| journal 增量恢复、snapshot | 已有 | 异常退出后可增量重建 |
| MP4 + 音频导出、0.5–8x 倍速、进度拖动 | 已有 | 导出管线保留 |
| 录制暂停 / 继续 | 本次补齐 | `InkRecorder.Pause/Resume`；暂停期间事件不入时间轴；`ElapsedMs` 扣除暂停时长；浮条新增「暂停录制 / 继续录制」 |
| 视图缩放 / 平移记录 | 本次补齐 | `InkController.ViewChanged` → `TimelineEventKind.ViewChanged`；`ClassroomScene` 记录 `ViewScale/ViewTx/ViewTy`；回放按事件应用视图 |
| 事件去重 / 排序 | 本次补齐 | 相邻同签名事件去重；早到事件按时间插入，避免时间轴倒退 |
| 启动恢复提示 | 本次补齐 | `ClassroomSessionStore.PendingJournalIds` 非空时，启动阶段提示存在未完成会话 |
| 会话列表元信息 | 本次补齐 | 展示录制时间、时长、页数、事件数 |
| 回放上一事件 / 下一事件 | 本次补齐 | 逐事件定位，便于讲解复盘 |
| 回放「仅板书 / 完整课堂」 | 本次补齐 | 仅板书模式隐藏 PPT 截图背景 |
| 手动添加书签 | 本次补齐 | 默认名可改；tooltip 展示「时间 · 第 N 页 · 摘要」；右键重命名/删除保留 |
| 异步定位取消 | 本次补齐 | CTS + 版本号 + 信号量；连续拖动只呈现最后目标 |
| 导出参数与磁盘检查 | 本次补齐 | 分辨率 1920×1080 / 1280×720 / 源；倍速 1x/2x/4x；仅导出板书；预计帧数与时长；按 1.2 倍估算磁盘空间，不足抛中文异常 |
| 导出失败 / 取消清理 | 本次补齐 | 失败保留会话可重试；成功或取消清理半成品 |
| 导出采样与倍速 | 本次补齐 | 帧时间 = `Start + i * (1000 / Fps) * Speed`；渲染尺寸可配；`BoardOnly` 跳过 PPT 截图 |
| 90 分钟真实长课 | 未验证 | 仅有单元测试与短时人工验证 |
| 高 DPI / 高对比度 | 未验证 | 未覆盖 200% 缩放与高对比度主题实机 |
| 真实音画同步 | 未验证 | 未做长时间音画漂移测量 |
| 1280×720 / 源尺寸 / 2x / 4x 真实 MP4 | 未验证 | 参数路径已通，缺少成片人工验收 |
| 导出期间继续书写 | 未验证 | 并发书写与导出稳定性未实测 |
| UI 交互人工验证 | 未验证 | 暂停按钮、仅板书切换、书签弹窗、拖动取消待人工走查 |
| WPS / 多屏场景 | 未验证 | 未覆盖 WPS 放映与多显示器组合 |

## 4. 数据格式与迁移〔历史归档：Session v2 / journal / snapshot 链路已移除；插件课堂录制为单个 `.icink` ZIP（`inkcanvas-classroom` v1），见用户指南〕

- **Session v2**：新增书签、`ViewChanged` 事件与 PPT 截图 payload；读取时对旧版 Session 做兼容处理，`Version < 2` 的会话在载入阶段补齐默认字段，不要求用户手动迁移。
- **journal / snapshot**：维持增量恢复语义，新增事件类型可被旧 journal 安全跳过；snapshot 仍用于加速恢复，不改变恢复优先级。
- **Board v3**：格式未变，本次未触碰板面存储结构。
- **PPT manifest v2**：格式未变，仅导出侧按需读取截图；`BoardOnly` 时不加载截图，缺失或失效时回退纯色背景。
- **兼容原则**：新增字段一律可缺省；旧数据可读、可回放、可导出；不引入破坏性重写。

## 5. 配置与 Feature Flag〔历史归档：时光机本体录制开关已随插件化移除；插件在「设置 → 模块与插件」中启停〕

AI 相关开关沿用 `AppSettings.Ai`（四项，默认全开，旧 `Settings.json` 缺少节点时回退默认）：

| 配置项 | JSON 键 | 默认 | 作用 |
| --- | --- | --- | --- |
| `EnableLocalCorrection` | `enableLocalCorrection` | `true` | 本地纠错 |
| `EnableEditablePaste` | `enableEditablePaste` | `true` | 可编辑贴回 |
| `EnableVerification` | `enableVerification` | `true` | 结果自检 |
| `EnableTwoStage` | `enableTwoStage` | `true` | 两阶段生成 |

- 时光机本体录制开关与自动保存配置已随插件化移除；插件在「设置 → 模块与插件」中启停，不沿用旧本体开关。
- 隐私（历史口径仍适用）：AI 与插件课堂数据全部本地处理与存储，无上传；导出文件仅写入用户选择的本机路径。

## 6. 测试报告〔历史归档：计数与用例归属为重构前基线，当前结果以插件测试项目与主执行者验收为准〕

构建命令与结果：

```bash
dotnet build src\InkCanvas.App -c Debug -p:Platform=x64
```

- 结果：**0 警告，0 错误**。

单元测试结果：

| 测试项目 | 结果 | 备注 |
| --- | --- | --- |
| `InkCanvas.Ai.Tests` | 79 / 79 通过 | AI 助手、纠错、粘贴、映射等 |
| `InkCanvas.Classroom.Tests` | 198 / 198 通过 | 含 `RecordingEnhancementTests` 17 项、`TimeMachineAdvancedTests` 17 项 |

已知基线问题：既有 `VectorParityTests` 仍不可编译，属于历史登记项，本次未改动、未计入上述通过数。该问题需单独排期修复，避免与本次交付混淆。

## 7. 未验证边界与后续建议〔历史归档：录制 / 回放 / 音频相关未验证项随本体链路关闭；插件待验证项以插件验收为准〕

**未验证边界（如实登记）**

1. 真实 90 分钟长课的录制、回放与导出稳定性。
2. 高 DPI（200% 及以上）与高对比度主题下的图标、书签轨道、浮窗布局。
3. 真实音画同步与长时间漂移。
4. 1280×720、源尺寸、2x、4x 组合下的真实 MP4 成片质量与耗时。
5. 导出进行中继续书写、切页、暂停等并发操作。
6. UI 人工走查：暂停按钮状态、仅板书切换、书签新增/重命名弹窗、拖动定位取消。
7. WPS 放映与多显示器场景下的 PPT 截图与回放一致性。

**后续建议**

1. 建立 90 分钟长课回归脚本，记录内存峰值、导出耗时、音画偏移与丢帧数，形成可对比的性能基线。
2. 补充高 DPI / 高对比度截图基线，纳入 UI 回归清单。
3. 为导出参数组合建立矩阵化验收（分辨率 × 倍速 × 仅板书），输出样片与耗时表。
4. 单独排期修复 `VectorParityTests` 编译基线，恢复该部分回归覆盖。
5. 对异步定位取消增加压力用例（快速连续拖动 100 次），确认仅最后一次目标被渲染。

## 8. 图标快速验收步骤

1. **主栏**：打开主浮条，确认「AI 识别（先框选内容）」显示文本「AI」，点击可打开 AI 助教；按钮尺寸与其他图标一致，无截断。
2. **次级菜单**：展开更多，确认「AI 助教」同样显示文本「AI」，tooltip 与无障碍名称均为「AI 助教」。
3. **课堂工具箱**：打开工具箱，确认「AI 助教」磁贴内为 15px「AI」文本，与上下排图标行高对齐。
4. **AI 助教浮窗**：打开浮窗，确认标题「AI 助教」前有 10px「AI」徽标，强调色显示正常。
5. **帮助入口**：确认浮条右侧问号仍为「操作指南」，与 AI 入口视觉可区分。
6. **主题与选中态**：切换亮/暗主题，选中 AI 按钮，确认前景色与背景选中态正常，无文字发虚或裁切。
7. **检索复核**：执行 `rg "Glyphs.Help" src`，确认仅「操作指南」一处命中，无 AI 问号残留。
## 9. 动态验收（2026-11-20 实机）〔历史归档：第 1、2 条为本体阶段短会话录制 / 回放取证；第 3 条强杀恢复链路已随本体移除，不迁入插件〕

实机环境：Windows 桌面端自构建 Debug 版本，鼠标以相对坐标移动模拟书写。下列 1–4 项为真机操作结果，5 项为未人工验证但由单测覆盖的边界。

1. **短会话录制（历史）**：打开画板，用相对鼠标移动画 3 笔；生成 journal（8 事件）与 snapshot；会话列表显示「板书 2026-11-20 23:13 · 录制 23:13 · 时长 0:07 · 1 页 · 8 事件」。
2. **回放画面（历史）**：时光机回放截图显示 3 条手绘墨迹（见 `D:\hw\artifacts\tm-replay-1x.png`），墨迹位置与笔顺与原板书一致。
3. ~~**强杀恢复**~~：该异常恢复链路（journal / snapshot 合并、启动恢复提示）已随本体移除，不迁入插件，本条取证不再适用。
4. **图标**：`ui-icons-main.png` / `ui-icons-ai.png` / `ui-icons-toolkit.png` 三处截图均显示「AI」文本图标；浮条右侧问号仍为「操作指南」，帮助入口保留；三处布局无截断、与其他图标行高对齐。
5. **未完成的人工操作（未验证，不声称通过）**：倍速切换与画面级拖动的真实交互、导出按钮的 UI 点击链路、导出期间继续书写。上述三类行为由单元测试覆盖（`ExportOptions` 参数校验、采样、Seek/定位、Checkpoint 写入）；MP4 导出以合成会话实测产出 48,167B 文件（含合成音轨，音轨链路已移除）。

## 10. 回归报告〔历史归档：计数为重构前基线〕

测试环境：`DOTNET_ROOT=D:\dotnet`、`PATH` 含 `D:\dotnet`、`NUGET_PACKAGES=D:\dev-cache\nuget`；执行前已结束运行中的 `InkCanvas` 进程，避免文件锁。

| # | 命令 | 真实结果 |
| --- | --- | --- |
| 1 | `dotnet test tests\InkCanvas.Ai.Tests` | **通过 79 / 79，失败 0，跳过 0**，耗时 76 ms |
| 2 | `dotnet test tests\InkCanvas.Classroom.Tests` | **通过 198 / 198，失败 0，跳过 0**，耗时 4 s |
| 3 | `dotnet test tests\InkCanvas.P0.Tests` | **通过 22 / 22，失败 0，跳过 0**，耗时 93 ms |
| 4 | `dotnet test tests\InkCanvas.Core.Tests` | **不可编译（预期失败）**：`VectorParityTests.cs` 与当前 `InkController` / `ShapeGenerator` / `InkStroke` / `BoardElementMath` 等实现签名不一致，报 `CS1061`、`CS0117`、`CS0234`、`CS0246`、`CS1729`、`CS0103` 等错误，0 项测试被执行 |

**基线欠账说明**：`InkCanvas.Core.Tests` 中 `VectorParityTests` 无法编译属于历史登记项，本次**未修改该测试文件与相关实现**，其失败不计入上述通过数，也不代表本次交付回归失败。该项需单独排期修复，恢复 Core 层向量回归覆盖。
## 11. 导出与恢复数值证据〔历史归档：11.1 讲解音频、11.3 恢复丢帧两节随本体移除；11.2 导出采样一致性若插件沿用，以插件测试为准〕

**证据来源口径**：以下数值均来自 `ExportSyncTests` 的合成会话断言与**本机 Media Foundation 实际编码产物**。它们不是真实课堂素材、也不是长时间真实录音的测量结果。帧率基准 30fps ⇒ 1 帧 = 33.3ms。

### 11.1 倍速音频变速对齐〔已随插件化移除：讲解音频录制与 AAC 音轨合入不迁入插件，本节数值作废〕

~~（原 WASAPI loopback 录制、WAV 重采样、AAC 音轨对齐与无音频设备降级条目均已删除，不再保留为现行功能证据。）~~

### 11.2 完整 / 区间导出一致性〔历史参考〕

| 场景 | 结果 |
| --- | --- |
| 完整区间 `[0,2000]` @30fps | 60 帧 |
| 区间 `[900,1700]` @30fps | 24 帧 |
| 2× 同区间 `[900,1700]` | 12 帧 |
| 区间首帧 vs 完整 `t=900ms` 帧 | 笔画数与序列化指纹**完全一致**（1 笔画） |
| 片段长度 1100ms @30fps | 33 帧（`ceil` 取整） |
| CTS 取消导出 | 不产出 MP4 |
| `CleanupArtifacts` | 删除 `.tmp` / `.part` 半成品，保留已完成 MP4 |

### 11.3 恢复丢帧〔已随插件化移除：journal 合并 / 崩溃丢帧窗口链路不迁入插件，本节数值作废〕

~~（原逻辑末事件、落盘末事件、`LastEventTimeMs`、恢复后末事件与丢帧窗口等条目均已删除，不再保留为现行功能证据。）~~

### 11.4 本节未验证项（历史归档，保留，不声称通过）

1. **导出期间继续书写**：并发书写与导出的稳定性未实测（仅单测覆盖参数与采样路径）。
2. **倍速 / 拖动 UI 画面级**：倍速切换与拖动定位的画面级表现未做人工走查。
3. **图标 16 / 20 / 24px 与高对比度**：多尺寸图标与高对比度主题下的可读性未验证。
4. **90 分钟长课**：仅短会话与单元测试，未做长课录制/导出。
5. **高 DPI**：200% 及以上缩放未实机覆盖。
6. **真实 WPS / 多屏**：WPS 放映与多显示器组合未验证。

## 12. 回归口径〔历史归档：计数为重构前基线，Core.Tests 欠账口径见当时记录〕

**环境**：`DOTNET_ROOT=D:\dotnet`、`PATH` 含 `D:\dotnet`、`NUGET_PACKAGES=D:\dev-cache\nuget`；执行前已结束运行中的 `InkCanvas` 进程以避开文件锁。

| # | 命令 | 真实结果 |
| --- | --- | --- |
| 1 | `dotnet build src\InkCanvas.App -c Debug -p:Platform=x64` | 0 警告，0 错误 |
| 2 | `dotnet test tests\InkCanvas.Classroom.Tests` | 通过 206 / 206（含本轮新增导出同步用例） |
| 3 | `dotnet test tests\InkCanvas.Ai.Tests` | 通过 79 / 79 |
| 4 | `dotnet test tests\InkCanvas.P0.Tests` | 通过 22 / 22 |
| 5 | `dotnet test tests\InkCanvas.Core.Tests` | **不可编译**：52 个错误，全部位于 `VectorParityTests.cs`，其它文件 0 错误，0 项测试被执行 |

**Core.Tests 排除口径（显式登记）**

- 该编译失败为**已知基线欠账**：52 个错误全部集中在 `VectorParityTests.cs`（签名/成员与当前 `InkController`、`ShapeGenerator`、`InkStroke`、`BoardElementMath` 不一致），同项目其它文件无错误。
- 因此 `Core.Tests` **显式排除、不计入本轮结果**；本轮**未修改任何测试文件**，也未改动相关实现来「迁就」该测试。
- 计数口径：本轮通过数 = Classroom 206 + Ai 79 + P0 22；Core.Tests 既不计入通过，也不计入失败，仅以「已知基线欠账」单列。
- 排除不代表该项已修复：需单独排期修复 `VectorParityTests` 以恢复 Core 层向量回归覆盖。

**数值来源边界（不夸大）**

- 第 11 节视频相关数值来自**合成会话**与本机 MF 编码；合成音轨（1s/440Hz WAV）对应的音频链路已随插件化移除，该部分边界不再适用。
- ~~音画偏移 0.00ms 是单次 500ms 成片的测量值……~~ 音频相关结论作废。
## 13. 端到端一致性与并发证据〔历史归档：时间轴 / 导出采样与并发书写用例若插件沿用，迁至插件测试项目复核；本节计数为重构前基线〕

**证据来源口径**：本节全部数值来自 `tests/InkCanvas.Classroom.Tests/EndToEndReplayTests.cs` 的**合成会话**（3 页、6 笔、21 个事件）与逻辑层 `InkTimeline` / `TimeMachineExporter` 断言。**不是真实课堂素材**，也不是截图人工比对。

**环境**：`DOTNET_ROOT=D:\dotnet`、`PATH` 含 `D:\dotnet`、`NUGET_PACKAGES=D:\dev-cache\nuget`；执行前已结束运行中的 `InkCanvas.exe`。

### 13.1 覆盖项与通过数

| # | 覆盖项 | Fact | 真实结果 |
| --- | --- | --- | --- |
| 1 | 跨页 / 背景 / 擦除 / 清空 / Undo / Redo / PPT / View 按事件顺序 | `SyntheticSession_CrossPageBackgroundEraseClearMatchesEventOrder` | 通过 |
| 2 | 同一时间点重复 `BuildFrame`（5 次 + 独立 `InkTimeline` 实例）指纹一致 | `RepeatedBuildFrame_IsBitIdenticalAcrossFivePasses` | 通过（11 个时间点） |
| 3 | 0.5× / 1× / 2× / 8× 模拟 elapsed 累加到达同一绝对时间点，帧指纹一致 | `PlaybackSpeed_ArrivalFrameMatchesDirectSeek` | 通过（4 倍速 × 8 时间点） |
| 4 | 倍速切换不改变事件顺序（`IndexAt` 单调） | `SpeedSwitching_KeepsIndexAtMonotonicAndFramesStable` | 通过（1684 个播放样本） |
| 5 | 随机 20 点升序拖动与顺序播放一致 | `RandomSeek_Ascending_MatchesSequentialPlayback` | 通过 |
| 6 | 随机 20 点降序拖动与顺序播放一致 | `RandomSeek_Descending_MatchesSequentialPlayback` | 通过 |
| 7 | 随机 20 点乱序拖动与顺序播放一致 | `RandomSeek_Shuffled_MatchesSequentialPlayback` | 通过 |
| 8 | 1× 导出采样时刻帧指纹一致 | `ExportSampling_At1x_MatchesBuildFrameFingerprint` | 通过（51 帧） |
| 9 | 2× 导出采样时刻帧指纹一致 | `ExportSampling_At2x_MatchesBuildFrameFingerprint` | 通过（26 帧） |
| 10 | BoardOnly 与完整课堂笔画集合相同，背景 / 截图由 `ShouldDrawPptBackground` 区分 | `BoardOnlyExport_UsesSameStrokeSetAsFullClassroom` | 通过（51 个采样时刻） |
| 11 | 导出期间并发书写不阻塞 | `ExportWhileWriting_MeetsStrokeLatencyBudget` | 通过（见 13.3） |
| 12 | 手动书签增 / 改名 / 删 / 摘要 / 描述 | `Bookmarks_AddRenameRemoveSummaryAndDescribe` | 通过 |
| 13 | PPT 载荷 slideId / index / 无截图 | `PptSlidePayload_EncodesSlideIdIndexAndEmptyScreenshot` | 通过 |
| 14 | Undo / Redo 恢复笔画且保持顺序 | `UndoRedo_RestoresStrokeInFrameOrder` | 通过 |
| 15 | 清空第 2 页后回页只影响该页 | `ClearedSecondPage_RemovesOnlyItsStrokesWhenReturning` | 通过 |
| 16 | 事件时间与 `IndexAt` 单调 | `TimelineIndexAt_IsChronologicalAndMonotonic` | 通过 |

**新增 Fact 数：16**；`dotnet test tests\InkCanvas.Classroom.Tests` 全量 **222 / 222 通过**（原 206 + 新增 16），0 失败 0 跳过。

### 13.2 跨页 / 背景 / PPT 数值一致性

| 时间点 | 页 | 背景 | PPT 页 | 当前页笔画（首点 X 标识） |
| --- | --- | --- | --- | --- |
| T1=250ms | 0 | Black | 1 | 10, 20 |
| T2=450ms | 1 | Grid | 1 | 空 |
| T3=850ms | 1 | Grid | 1 | 40, 50（第 1 笔已擦除） |
| T4=950ms | 1 | Grid | 1 | 40（Undo 移除第 3 笔） |
| T5=1050ms | 1 | Grid | 1 | 40, 50（Redo 恢复） |
| T6=1250ms | 2 | Coordinate | 2（无截图） | 空 |
| T7=1350ms | 2 | Coordinate | 2（无截图） | 60 |
| T8=1750ms | 1 | Coordinate | 2（无截图） | 空（第 2 页已被清空） |

- `SceneAt` 的页 / 背景 / PPT 页 / `PptScreenshotPath` 与 `BuildFrame` 笔画集合在每个时间点同时断言，顺序错位即失败。
- PPT 载荷由 `EncodePptSlideChanged(202, 2, "")` 构造，解码后 `SlideId=202`、`Index=2`、`Screenshot=""`；回放沿用时清空上一页截图，不沿用旧图。

### 13.3 倍速 / 拖动 / 导出 / 并发数值

| 项目 | 数值 | 判读 |
| --- | --- | --- |
| 倍速到达一致性 | 0.5× / 1× / 2× / 8× × 8 个绝对时间点 | 到达帧与直接 `BuildFrame(t)` SHA-256 指纹相同 |
| 倍速切换样本 | 1684 个 | `IndexAt` 全程单调不减，末事件索引 20 |
| 随机拖动 | 20 个唯一时间点 × 升序 / 降序 / 乱序 | 每点两次 `BuildFrame` 指纹相同，且等于顺序播放基准 |
| 1× 导出采样 | 51 帧，步长 33ms，末帧 1667ms | 每个采样时刻帧指纹等于直接定位 |
| 2× 导出采样 | 26 帧，步长 67ms，末帧 1667ms | 每个采样时刻帧指纹等于直接定位 |
| BoardOnly vs 完整课堂 | 51 个采样时刻 | 笔画集合与指纹完全相同；`ShouldDrawPptBackground` 返回 false / true |
| 并发导出期间书写 | 100 笔 | 单笔最大 **0.069ms**（< 50ms），总耗时 **0.095ms**（< 2000ms） |
| 并发重叠 | 100 / 100 笔 | 100 笔全部在导出任务未完成时提交完成 |
| 导出结果 | Media Foundation 可用 | `ExportAsync` 正常完成，生成非空 MP4（640×480 / 30fps / 2s 会话） |

**「画面级」证明方式**：以 `BuildFrame` 输出的笔画内容（`StrokeJsonCodec` 序列化）计算 SHA-256 帧指纹、以当前页笔画首点 X 集合证明「画了哪些笔」、以 `SceneAt` 的页 / 背景 / PPT 页 / 截图路径数值证明「画在什么场景」。**未使用截图，也未做人工目视比对**；因此可自动化、可重复，但只覆盖逻辑渲染源数据，不等于最终像素级外观。

**并发测量边界**：并发书写使用独立的 `InkDocument`（不经 UI 线程），测量的是笔画提交逻辑耗时；导出在 `Task.Run` 后台渲染并真实编码 MP4。它证明「导出编码不阻塞笔画数据结构提交」，**不证明**真实 UI 线程消息循环、输入延迟或渲染帧率。

### 13.4 本节未验证项（历史归档，保留，不声称通过）

1. **真实 UI 人工操作**：倍速按钮、时间轴拖动条、书签面板的真实点击与视觉反馈未做人工走查；本节均为无 UI 的逻辑断言。
2. **90 分钟长课**：仅 2s 合成会话，未做 90 分钟录制 / 导出 / 拖动压力测试。
3. **高 DPI / 高对比度**：200% 及以上缩放与高对比度主题未实机覆盖。
4. **真实 WPS / 多屏**：WPS 放映翻页、真实截图路径与多显示器组合未验证。