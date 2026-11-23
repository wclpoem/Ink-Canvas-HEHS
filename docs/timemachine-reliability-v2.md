# 板书时光机可靠性升级与 AI 图标统一（本轮实施记录）

> **【历史归档 · 2026-11-21 实施记录】** 本文件记录的是时光机尚在本体内置阶段的可靠性改造，正文不再代表现行架构。时光机已重构为独立插件模块「时光机」（Id `inkcanvas.time-machine`）：§2.3 完整性校验 / `.sha256`、§2.4 旧版 `.bak` 迁移备份、§7 备份回滚，以及异常恢复、录制落盘恢复、讲解音频 / 音画同步等内容已随本体删除，不迁入插件；事件序号、稳定排序、检查点、导出性能等结论仅作为插件沿用参考。AI 图标部分为现行有效记录。现行插件形态见 `docs/timemachine-user-guide.md`。

- 实施日期：2026-11-21
- 分支：`完善AI助教、课件联动与课堂练习闭环`
- 性质：增量优化；未重写既有录制 / 回放 / 导出路径，未改动白板书写、多页画板、PPT-WPS 批注与撤销重做
- 前置审查：`docs/timemachine-gap-audit.md`

## 1. AI 图标统一

### 1.1 改动

| 文件 | 改动 |
| --- | --- |
| `src/InkCanvas.Controls/Controls/AiGlyph.cs`（新增） | 「AI」标记唯一资源层：`Text` 常量、16/20/24px 档位（`PresetFor`）、徽标档位（`PresetForLabel`）、固定度量盒工厂（`CreateFramed`） |
| `src/InkCanvas.Controls/Controls/TouchIconButton.cs` | 删除 `Text = "AI"` / 12px 硬编码，改 `AiGlyph.CreateLabel(AiGlyph.ButtonLabelSize)` |
| `src/InkCanvas.App/Views/ToolkitWindow.cs` | 删除 24×22 盒内 15px 硬编码，改 `AiGlyph.CreateFramed(24, 22)` |
| `src/InkCanvas.App/Views/AssistantWindow.cs` | 标题徽标改 `AiGlyph.CreateLabel(BadgeLabelSize)`；新增首次引导标记联动 |
| `src/InkCanvas.App/Views/SlideNavigatorWindow.cs` | 课件页 AI 角标改统一工厂 |
| `src/InkCanvas.Core/Settings/AppSettings.cs` | 新增 `ai.hasSeenLassoHint`（默认 false，旧设置缺键仍提示一次） |
| `src/InkCanvas.App/Services/ShellController.cs` | `ShowLassoAiHintOnce`：首次打开 AI 浮窗时经现有 notice 行提示「框选内容后，可使用 AI 识别公式和图形。」约 2.6 秒自动消失，随后写盘不再重复 |

### 1.2 语义与无障碍

- AI 入口：`FloatingBar` 主栏「AI 识别（先框选内容）」与次级「AI 助教」，均使用 `Glyphs.Ai` 哨兵 → `TouchIconButton` 统一渲染。
- 帮助入口：`FloatingBar` 右侧问号仍为「操作指南」（`Glyphs.Help`），未被替换或删除，AI 与帮助语义保持独立。
- 无障碍名称：浮条按钮沿用 tooltip 作为 `AutomationProperties.Name`，读作「AI 识别（先框选内容）」「AI 助教」，不读作问号。
- 状态表达：AI 浮窗沿用「状态点 + 状态文字 + 进度环」表示识别中 / 生成中 / 完成 / 出错，未把多状态塞入图标。

### 1.3 实机取证（2026-11-21，Debug 构建）

| 检查点 | 结果 | 证据文件（`D:\hw\artifacts\`） |
| --- | --- | --- |
| 主浮条 AI 按钮 | 显示文本「AI」，与相邻 Fluent 图标同排，无截断 | `ai-icon-v2-bar.png`、`ai-icon-v2-button.png` |
| 悬停态 | 前景与背景正常，无发虚或裁切 | `ai-icon-v2-button-hover.png` |
| 次级菜单 | 「AI 助教」显示 AI 标记；「操作指南」仍为问号 | `ai-icon-v2-secondary.png`、`help-icon-v2-secondary.png` |
| 课堂工具箱 | AI 磁贴显示 AI 标记，与上下排图标行高对齐 | `ai-icon-v2-toolkit.png`、`ai-icon-v2-toolkit-tile.png` |
| AI 浮窗头部 | 徽标「AI」+ 标题「AI 助教」，状态「已完成 / 未验证」 | `ai-window-v2.png` |
| 无障碍名称 | AI 按钮读作「AI 识别（先框选内容）」「AI 助教」；帮助入口读作「操作指南」 | UIAutomation 枚举输出 |
| 暗色主题 · AI 浮窗 | 深色背景、浅色文字，AI 徽标清晰可读 | `ai-window-v2-dark.png` |
| 暗色主题 · 主浮条 | **已适配**：深灰底 `#2B2C2F` + 浅色「AI」与图标 | `ai-icon-v2-bar-dark4.png` |
| 亮色主题 · 主浮条 | 与改动前一致（白底黑字），主题判断未误判 | `ai-icon-v2-bar-light-final2.png` |
| 高对比度 | **已实机取证**：黑底白字，AI 与图标清晰，选中态为高对比色 | `ai-icon-v2-bar-highcontrast.png` |
| 真实 PPT 放映 | `p0-test.pptx` 共 3 页，COM 翻页至 3/3；覆盖层与「PowerPoint 导航」同步出现 | `ppt-slideshow-overlay.png` |
| 窗口缩放 125% | 浮条 858×70 → 1072×88，AI 按钮 50×47 → 62×62；标记随比例放大、无截断、与相邻图标对齐 | `ai-icon-v2-bar-scale125.png`、`ai-icon-v2-button-scale125.png` |
| 窗口缩放 200%（设置值） | 单屏空间不足时实际渲染降级为 150%：浮条 1287×105，整条居中完整可见，全部按钮与「AI」无裁切；用户设置值仍保留 200% | `ai-icon-v2-fullscreen-scale200-fixed.png` |

暗色与缩放取证方式：临时修改 `Settings.json`（`appearance.theme = 2`、`enableViewboxFloatingBarScaleTransform = 125`），启动截图后**均已恢复原值**（0 / 100）。

**启动前置**：直接启动会弹出「You must install or update .NET to run this application.」；设置 `DOTNET_ROOT=D:\dotnet` 后可正常启动。该提示是运行环境变量缺失，**不是应用缺陷**。

**仍未验证（历史记录）**：200% 及以上系统 DPI（已尝试修改 `PerMonitorSettings.DpiValue` 与 `LogPixels` 并重启资源管理器，Win11 下需注销才生效，未执行注销；**注册表已恢复原值**）；真实课堂素材的 90 分钟录制（归属插件压测）；WPS 放映与多显示器（本机仅 1 台显示器）。~~真实音轨（WASAPI/AAC）成片音画同步~~ 音频链路已移除，该项作废。

## 2. P0：录得全、回得准〔历史归档：「异常可恢复」相关条目已随本体移除，不迁入插件〕

### 2.1 事件模型

`TimelineEvent` 新增三个可缺省字段，旧 JSON 缺字段时按默认值兼容：

| 字段 | 含义 | 兼容策略 |
| --- | --- | --- |
| `SessionId` | 事件所属会话 | 加载 / 追加时按 `ClassroomSession.Id` 补齐 |
| `Seq` | 会话内单调序号，同毫秒事件的稳定排序键 | 旧数据为 0 时按原数组顺序补 1..N |
| `PptSlideId` | 课件归属（PPT 幻灯片 id，0 表示非课件） | `PptSlideChanged` 时从载荷解析 |

`InkRecorder.Append` 统一分配 `SessionId` 与 `Seq`，外部事件源（PPT / 点名 / 倒计时 / 练习）不得自行编号。

`SessionCheckpoint` 新增 `Seq`，检查点同时用时间与序号锚定。

新增 `TimelineEventKind.AiStep`：AI 识别 / 求解 / 贴回 / 纠错可作为独立事件记录，不参与基础板书回放。

### 2.2 稳定排序

`InkTimeline` 构造时统一归一化：

1. 空事件剔除；
2. 缺失 `SessionId` / `Seq` 补齐；
3. 若时间倒退或同毫秒 `Seq` 逆序，按 `(TimeMs, Seq, 原索引)` 稳定重排，并清空失效检查点。

缺失序号时按数组顺序编号，已有序号即使非递增也保留（由排序决定位置），避免重编号改变「同毫秒先后」语义。

### 2.3 完整性校验与原子写入〔历史归档：`.sha256` 校验旁车 / `.bak` 回退已随本体移除，不迁入插件〕

- 正式会话与快照均写 `<文件>.sha256` 旁车校验，先写 `.tmp` → 替换正式文件 → 写校验，避免半文件。
- 加载时校验不匹配：优先用 `<文件>.bak` 迁移前备份；无备份则抛 `InvalidDataException`，不把损坏内容当作正常会话。
- 旧会话没有校验文件时按「未校验」放行，保证向后兼容。
- 删除会话 / 淘汰旧会话时同步删除 `.sha256`、`.bak`、snapshot、journal。

### 2.4 旧版迁移备份〔历史归档：旧会话 `.bak` 迁移链路已随本体移除，不迁入插件〕

加载时若原始 JSON 不含 `version` 属性或 `Version < 2`：

1. 先把原文件复制为 `.bak`（只写一次，不覆盖更早备份）；
2. 内存中补齐 `Bookmarks`、把 `Version` 提升到 2；
3. 不写回原文件，下一次正常保存才落新版格式。迁移失败不会改动历史记录。

### 2.5 回放只读

- 回放仍使用独立 `InkReplayController` 与 `InkTimeline.BuildFrame`，框架路径不写回事件流。
- 新增回归测试 `BuildFrame_DoesNotMutateEventStream`：多时间点反复定位后，事件 `Seq/TimeMs/Kind/StrokeId` 序列完全不变。

### 2.6 从当前时刻继续板书

本轮**不提供**同会话续写入口：任何后续书写必须作为新会话录制，从机制上避免修改原始记录。

## 3. P1：长课性能与定位体验〔历史归档：录制落盘 / 恢复 / 音画同步行已移除；回放定位与导出耗时数字仅作插件基线参考〕

### 3.1 已补

| 能力 | 实现 |
| --- | --- |
| 键盘操作 | 空格播放/暂停、← → 逐事件、↑ ↓ 跳章节书签、+ / - 切换倍速、Home 回到开头；界面显示快捷键提示 |
| 时间轴缩放 | 1× / 2× / 4×，同步缩放进度条、书签轨道与关键位置刻度；底部区域可横向滚动，不撑破窗口 |
| 关键位置刻度 | 每 10% 时长绘制刻度并标注时间，配合 4× 缩放做长课粗定位 |
| 定位预算 | 20,000 事件（等价 90 分钟）会话：冷启动定位 153ms，10 次定位合计 13ms（热缓存），托管内存增量 43.6MB；惰性解析改造前分别为 339ms / 834ms / 47.7MB |
| 90 分钟逐帧回放 | 20,010 事件 / 10 页，30fps 共 162,001 帧：总耗时 106.96s（平均 0.66ms/帧）；托管内存 55.6MB → 峰值 57.1MB，末次采样仍为 57.1MB；冷启动定位 186.8ms / 当前页 2,000 笔画 |
| 90 分钟录制落盘 | 54,000 事件写入 191.1ms（0.0035ms/事件），journal 23.98MB；保存 213.3ms，恢复 462.1ms，总 1.69s；托管内存增量 6.68MB |
| 真实 MP4 导出 | 120s 会话 / 3,600 帧 / 640×480@30fps / 仅板书：10,329ms，2.87ms/帧，134,900 字节 |
| 音画同步（合成音轨） | 2× 导出：视频 500.0ms、音频轨 512.0ms、偏差 0.00ms（1 帧 = 33.3ms）；无音频设备时静默降级为纯视频 |
| 崩溃丢帧窗口 | 逻辑末事件 5000ms、journal 末事件 4000ms → 崩溃丢帧 1000ms（上限 2000ms） |

### 3.2 分块加载

- **笔画按需解析 + 缓存上限：已实现。** `InkTimeline` 不再在首次定位时反序列化全部 `StrokeJson`，改为维护 `id → PageIndex` 轻量索引，仅对当前帧存活且同页的笔画按需解析；`_registry` 上限 4096，超出时 FIFO 淘汰，并保护当前帧结果集不被移除。
- **文件级流式分块加载：仍未实现。** 会话 JSON 仍一次性读入；内存约束来自「按需解析 + 缓存上限 + 会话列表上限（20 条）+ 快照周期（2 秒）」。不声称具备流式分块。

## 4. P2：课堂复盘与导出

| 能力 | 实现 |
| --- | --- |
| 按章节选片段 | 导出对话框新增「片段（按章节）」下拉：选中书签后自动填入开始时间，结束时间取下一书签或会话末尾，仍可手动微调 |
| 光标叠加 | `ExportOptions.ShowCursor`：在导出帧上绘制最近一笔末端的高亮圆点（可关闭） |
| 章节信息叠加 | `ExportOptions.ShowChapterInfo`：左上角绘制目标时刻之前最近书签的「时间 · 页 · 摘要」（可关闭） |
| 隐私提示〔历史参考〕 | 当时导出对话框写明「姓名、头像与讲解音频均在本机处理，不会上传」；插件化后讲解音频不再存在，隐私口径见新用户指南 |
| 取消 / 重试 / 清理 | 沿用既有实现：后台编码、进度、取消、失败重试、成功后清理半成品 |

`ExportOptions` 新增参数均为可选、默认关闭，既有调用与预览计算保持兼容。

## 5. 测试与数值证据（真实命令输出）〔历史归档：校验 / 备份 / 迁移 / 落盘恢复用例已删除，回放与导出用例迁至插件测试项目〕

环境：`DOTNET_ROOT=D:\dotnet`、`PATH` 含 `D:\dotnet`、`NUGET_PACKAGES=D:\dev-cache\nuget`；构建前已结束运行中的 `InkCanvas.exe`。

| # | 命令 | 结果 |
| --- | --- | --- |
| 1 | `dotnet build src/InkCanvas.App/InkCanvas.App.csproj -c Debug -p:Platform=x64` | 0 警告，0 错误 |
| 2 | `dotnet test tests/InkCanvas.Classroom.Tests` | 通过 269 / 269 |
| 3 | `dotnet test tests/InkCanvas.Ai.Tests` | 通过 132 / 132 |
| 4 | `dotnet test tests/InkCanvas.P0.Tests` | 通过 33 / 33 |
| 5 | `dotnet test tests/InkCanvas.Core.Tests` | 通过 148 / 148 |

本轮新增 17 项：

| 测试 | 覆盖 |
| --- | --- |
| `Normalize_FillsSessionIdAndSeq_ForLegacyEvents` | 旧事件补齐会话归属与序号 |
| `Normalize_ReordersOutOfOrderEvents_ByTimeThenSeq` | 时间乱序稳定重排 |
| `SameMillisecondEvents_AreOrderedBySeq_NotInsertionOrder` | 同毫秒按序号而非插入顺序 |
| `Checkpoints_CarrySeq_ForStableRelocation` | 检查点携带序号 |
| `CursorPoint_ReturnsLastPointOfLatestStrokeOnCurrentPage` | 导出光标位置 |
| `BuildFrame_DoesNotMutateEventStream` | 回放只读 |
| `Save_WritesChecksumAndLoadVerifiesIt` | 原子写 + 校验 |
| `TamperedFile_WithoutBackup_IsRejected` | 无备份损坏文件拒绝加载 |
| `TamperedFile_WithBackup_FallsBackToBackup` | 有备份时回退 |
| `LegacySessionWithoutVersion_GetsBackupAndUpgradedFields` | 旧版迁移与备份 |
| `NinetyMinuteSession_SeekIsWithinOneSecond` | 90 分钟等价压力测试与定位预算 |
| `BuildFrame_90MinutePlayback_30Fps_KeepsTimeAndMemoryBounded` | 90 分钟 × 30fps 逐帧回放吞吐与内存有界 |
| `Export120SecondSyntheticSession_MeasuresRealMp4Timing` | 真实 MP4 导出耗时与成片大小 |
| `JournalAndStore_90Minutes_54000Events_WritesAndRecovers` | 90 分钟录制落盘吞吐与恢复一致性 |
| `AiGlyph_IsTheSingleResourceAndCoversIconSizes` | 图标资源层与尺寸档位 |
| `CallSites_UseAiGlyph_AndDoNotHardcodeTheMark` | 调用点不再硬编码 |
| `HelpEntry_StaysSeparateFromAiEntry` | AI / 帮助入口分离 |

90 分钟压测输出（测试日志原文）：

```
90 分钟会话：20000 事件，冷启动定位 153ms，10 次定位合计 13ms，托管内存增量 43.6MB
```

逐帧回放压测输出（测试日志原文）：

```
事件数=20010；笔画数=20000；页数=10
冷启动 BuildFrame(2700000ms) 耗时=186.8 ms；笔画数=2000
总帧数=162001；逐帧总耗时=106958.7 ms；平均每帧=0.660 ms（上界 180000 ms）
内存采样(MB)=[55.6, 56.7, 57.1, 57.1, 57.1, 57.1, 57.1, 57.1, 57.1, 57.1]
峰值内存=57.1 MB；最后采样=57.1 MB
```

惰性解析改造前后对比（同一测试、同一机器）：冷启动 339ms → 153ms，10 次定位 834ms → 13ms，托管内存增量 47.7MB → 43.6MB。

按验收条款逐项列出的可复核用例与通过记录见 `docs/timemachine-test-evidence.md`（分类命令运行 91 项，通过 91，失败 0；原始输出留存 `D:\hw\artifacts\test-evidence-raw.txt`）。

**口径说明**：该数值来自合成会话（20,000 个笔画事件，无 UI、无 Win2D 栅格化），不是真实课堂 90 分钟录像，也不是实机帧率 / 进程内存测量。

## 6. 未验证边界（不声称通过）

1. 真实 90 分钟课堂录制、回放与导出的帧率、内存与丢事件情况。
2. 200% 及以上 DPI、高对比度主题下的图标与时间轴布局。
3. 本轮图标改动的实机截图（应用启动后数秒内退出，未完成截图）。
4. 真实光标叠加 / 章节信息叠加的 MP4 成片人工目视验收。
5. 真实音画长时间漂移；现有证据为合成音轨短片段。
6. WPS 放映、多显示器组合。
7. 事件级分块加载与 LRU 淘汰（未实现）。

## 7. 回退方式〔历史归档：校验 / 备份回滚条目已随本体移除；事件字段与导出选项回退说明仅作历史参考〕

- AI 图标：`AiGlyph` 为标准类，回退只需把调用点恢复为原文本构造；不影响数据。
- 事件字段：`SessionId` / `Seq` / `PptSlideId` 均可缺省，旧版本程序读取新会话时会忽略未知字段。
- 完整性校验：删除 `<id>.json.sha256` 即回到无校验加载路径。
- 迁移备份：`.bak` 为原格式副本，可直接改名回 `.json` 回滚。
- 导出选项：新增参数默认 false，旧调用行为不变。