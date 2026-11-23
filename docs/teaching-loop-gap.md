# 现状与功能差距清单（教学闭环）

> 审查日期：2026-11-21｜分支：`完善AI助教、课件联动与课堂练习闭环`
> 结论：三模块基础能力可复用，但「教学模式 / 拼题 / 批注导航 / 练习闭环 / 讲义导出」均为新增；建议按需求第九节分三阶段。

## 1. AI 助教

### 可复用
- `AssistantMachine`（状态机/事件/步骤 Steps）、`AssistantSession`（Text/Formula/Steps/Annotations/Regions/Confidence/Verified）
- 局部纠错：`AssistantRegion`、`RegionTextMapper`、`LocalCorrectionFlow`
- 流式：`AiClient`（SSE + mock + 两阶段 InkOnly/WithImage）
- 渲染：`AssistantWindow`（Markdown/公式/步骤/自检徽标/AI 图标）
- 贴回：`PasteCardRenderer` + `AiPasteMetadata`（可双击编辑）

### 缺失
| # | 缺口 | 影响 | 方案 | 工作量 |
|---|---|---|---|---|
| A1 | 四种教学模式（只给提示/分步引导/完整解答/教师解析） | 无法控制是否泄露答案；课堂投屏不合规 | 新增 `AssistantTeachingMode`；Prompt/事件按模式生成；步骤默认折叠，教师点“下一步”才展示；课堂默认 Hint|StepByStep | L |
| A2 | 跨选区拼题 | 无法把题干+图形+条件合并一题 | `ProblemContext{Regions[], Thumbnails[], Order}`；多图请求（payload images[]）；缩略图排序/删除/单区重识别；切页提示保留 | L |
| A3 | 图形标注联动 | Annotations 仅存储，不能高亮/逐步 | 新增 `AnnotationStep{Index, Points, Highlight}`；原图高亮层 + 显示/隐藏；定位不确定时只显示文字并提示 | M |
| A4 | 模式切换重复请求 | 切换模式可能重新识别 | 缓存同一 `ProblemContext` 的识别结果；模式只改渲染/裁剪 | S |

### 风险
- 多图请求需扩展 `AssistantRequest`（保持 6.1 字段向后兼容，新增 `images[]`）。
- 分步展示不得让后续步骤提前进入 DOM（防泄露）；需裁剪渲染而非仅隐藏。

## 2. PPT/WPS 授课导航与讲义

### 可复用
- `PptInkSession`（slideId/页码/截图/transition/预取缓存）、`PptInkFileStore`（`slide-{SlideId}.icink` + manifest v2）、`PptNavigationWindow`（四角翻页器）、`SlideInkMigrator`、`PresentationIdentity`
- 批注归属已有 slideId 锚定，插页/删页/调序已由迁移与 inspector 处理

### 缺失
| # | 缺口 | 影响 | 方案 | 工作量 |
|---|---|---|---|---|
| P1 | 批注导航面板 | 无法按批注/收藏快速跳页 | 新增 `SlideContentIndex`（slideId→hasInk/Favorite/HasAi/Thumbnail/Text）；缩略图面板 + 筛选（全部/批注/收藏）+ 跳转 | L |
| P2 | 收藏页/AI 讲解页索引 | 讲解结果无法回查 | `SlideContentIndex` 增加 Favorite、AiSessionIds；AI 贴回/讲解关联 slideId | M |
| P3 | 动画步骤状态 | 同页不同动画阶段混淆 | `PptSnapshot.AnimationStep`（COM 可读则记录，否则 0 降级）；AI 会话键含 slideId+step | M |
| P4 | 媒体状态处理 | 翻页/黑屏后课件音视频可能继续 | 翻页/黑屏/退出时调用 COM 暂停媒体（失败降级，不影响翻页/批注） | M |
| P5 | 课件文本上下文 | AI 缺少标题/本页文本/前后页 | COM 提取页文本（失败降级）；`SlideContentIndex.Text` 注入 AI 请求 `context`（不覆盖框选） | M |
| P6 | 讲义导出（PDF/图片） | 课后无法沉淀 | `HandoutExporter`：课件页截图 + 批注 + AI 解析 → PDF（PdfSharp）或 PNG 合成；选页/仅批注/含解析；后台 + 预览 + 重试 | XL |

### 风险
- PDF 需新增依赖（PdfSharp/MigraDoc），需 NuGet 还原（网络受限）；可先 PNG 讲义、PDF 后续。
- COM 提取动画/文本不一定可用，必须安全降级。
- 讲义合成本机可验证（合成截图），真实 PPT 页面渲染需实机。

## 3. 课堂工具箱与练习闭环

### 可复用
- `ClassroomRoster`（名单/抽取/贴板）、`CountdownWindow`（Started/Stopped/Ended）、`SpotlightWindow`/`MagnifierWindow`、`ToolkitWindow`、`PasteCardRenderer`；课堂信号（点名 / 倒计时结束等）经宿主事件接入「时光机」插件生成章节书签

### 缺失
| # | 缺口 | 影响 | 方案 | 工作量 |
|---|---|---|---|---|
| T1 | 场景预设 | 每次手工组合工具 | `ClassroomPreset{Name, Tools[], Layout, Timer}`；讲题/练习/小组/总结内置 + 可保存个人预设 | M |
| T2 | 变式题→练习闭环 | 无法从 AI 生成练习 | `ExerciseFlow`：变式题（基础/提高）→ 预览编辑 → 贴入新画板页（不覆盖原题）→ 启动倒计时 → 练习场景；结束快照+书签 | L |
| T3 | 分组积分 | 无小组活动数据 | `GroupScoreService`（共用名单、随机分组、积分/加减/重置、本地持久化） | L |
| T4 | 工具结果贴板 | 仅点名可贴 | 抽奖/分组/练习结果文本卡片（复用 AiPasteMetadata/Steps） | S |
| T5 | 工具配置持久化 | 位置/收起/配置下次丢失 | Toolkit/Timer/Roster 设置写入 Settings（版本化） | M |
| T6 | 聚光灯/放大镜贴图+AI | 无法把聚焦区域交给 AI | 聚焦区域截图 → 贴板（携带来源页）或直接 AI 请求 | M |

### 风险
- 场景预设需“组合现有工具”，不得复制业务逻辑；工具窗口单实例/焦点管理需复用。
- 变式题依赖 AI（mock 可验），不得自动覆盖原题。

## 4. 架构与数据

| 模块 | 现状 | 计划 |
|---|---|---|
| TeachingSession | 已实现 | 统一课堂会话 ID（关联 PPT/画板/AI/练习/工具/导出），版本化 JSON |
| AIProblemContext | 已实现（`ProblemContext`） | 多选区+缩略图+顺序+slideId/pageId+识别缓存+序列化 |
| SlideContentIndex | 已实现 | slideId→文本/缩略图/批注/收藏/AI 会话 |
| ClassroomPreset | 已实现 | 场景预设（内置+个人） |
| GroupScoreService | 已实现 | 分组/积分（本地） |
| HandoutExporter | 已实现 | 讲义 PNG + 最小 PDF（无外部依赖） |

- 数据版本：AiPasteMetadata v1、Board v3、Ppt manifest v2；新增结构均带 `Version` 与兼容读取。原 `ClassroomSession v2` 录制会话格式已随时光机插件化移除，课堂录制改由插件保存为 `.icink`（`inkcanvas-classroom` v1）。
- AI 请求可取消（已有 Abort/CTS）；切题/切页/关窗需用 sessionId+generation 丢弃过期响应（复用 SlideSwitchGate 思路）。

## 5. 改造风险总览
1. 范围极大：P0 约 15+ 功能点；一次全做风险高，建议按九节分阶段。
2. 真实环境：WPS/多版本 PPT、动画/媒体、真实 AI、PDF 渲染需实机；本机用合成课件截图/mock 验证并标注。
3. 既有保护：批注归属/撤销/画板数据不得改动既有格式；时光机已拆为独立插件，其录制格式不影响本体画板与课件数据，新字段只追加。
4. UI 复杂度：批注导航缩略图、拼题缩略图、场景预设均需新面板，需复用现有浮窗/主题/无障碍模式。

## 6. 建议实施顺序与工作量
- **第一阶段**（推荐首个交付）：A1 教学模式 + A2 跨选区拼题 + P1 批注导航面板（约 L+L+L）
- **第二阶段**：T2 变式题闭环 + T1 场景预设 + 倒计时快照/书签（L+M+S）
- **第三阶段**：P6 讲义导出 + T3 分组积分 + T6 聚光灯/放大镜 AI（XL+L+M）
- P2（学生端/课堂摘要）仅接口预留，不实施。

## 7. 待确认
1. 平台范围：仅 WinUI（当前分支）还是同时含鸿蒙 ArkTS？
2. 本次范围：只做第一阶段，还是 P0 三块全量？
3. 验收环境：沿用“本地模拟/合成 + 真实项标注未验证”，还是要求真实 PPT/WPS/AI 联调？
---

## 8. 实施结果（2026-11-21）

按需求第九节三阶段实施，均为增量改造，未改动既有白板书写、撤销/重做、批注归属与画板历史数据格式；时光机相关录制链路后续已拆为独立插件。

### 第一阶段（已实现）
- **A1 四种讲题模式**：`AssistantTeachingMode` + `AssistantTeachingPolicy`；分步模式在渲染前裁剪未揭示步骤与最终答案，课堂默认 `step`；切换模式只重排展示，不重发请求（`SetTeachingMode`/`RevealNextStep`）。
- **A2 跨选区拼题**：`ProblemContext`/`ProblemContextPolicy`（追加、删除、排序、外接矩形、首图兼容、序列化）；浮窗横向缩略图 + 前后移动/删除/单区「↻」重新识别；切页/换画板页给出保留提示，不静默丢失。
- **P1 批注导航面板**：`SlideContentIndex`（slideId→批注/收藏/AI/缩略图/文本）+ `SlideNavigatorWindow`（全部/仅批注/收藏筛选、跳页、收藏、当前页/总页/放映中状态）；缩略图按 `thumb-{slideId}.png` 持久化。
- **A3 图形标注联动**：`AnnotationLayerPolicy` 生成与步骤对应的标注项；浮窗缩略图叠加矢量折线，显示/隐藏与逐步揭示同步；锚点缺失或点集退化时只显示文字并给出原因；贴回沿用 `AiPasteMetadata.Annotations` 独立标注层。
- **A4 不重复识别**：模式切换复用当前会话；拼题上下文变化才重新取图/请求。

### 第二阶段（已实现）
- **T1 场景预设**：`ClassroomPreset`/`ClassroomPresetLibrary`（讲题/练习/小组/总结四套内置；`Plan()` 只输出打开/收起工具动作，不复制业务逻辑）；工具条新增「切换场景」；`PresetWindow` 提供工具勾选、倒计时与保存，个人预设写入 `classroom.customPresets` 并参与场景轮换。
- **T2 变式题→随堂练习闭环**：`ExerciseFlowPolicy`（基础/提高、变式请求、题干/答案/知识点解析、练习计划）；AI 浮窗「基础变式/提高变式」→ 预览编辑对话框（可改题干与答案）→「开始练习」插入新画板页（原题不覆盖）→ 贴练习卡片 → 应用练习场景 → 启动倒计时。
- **倒计时结束**：宿主发出练习结束信号；板书快照与「练习结束」章节书签由「时光机」插件在录制时决策与生成，普通计时保持原有语义。

### 第三阶段（已实现）
- **P6 讲义导出**：`HandoutComposer`（全部/仅批注/选中页、页面尺寸）、`HandoutExportService`（课件页 PNG + 教师解析文本合成、失败重试）、`HandoutPdfBuilder`（Flate 压缩 RGB 的最小 PDF，无外部依赖）；导出前对话框预览首屏与页勾选；后台异步执行，不修改原课件。
- **T3 分组积分**：`GroupSplitPolicy`（按组数/按人数、余数处理、随机种子）+ `GroupScoreService`（共用 `roster.json` 名单、加减分、本轮重置、排名、文本卡片）；`GroupScoreWindow` 提供操作界面，`%APPDATA%\InkCanvas\groups.json` 本地持久化。
- **T5 工具配置**：`AppSettings.classroom`（场景、倒计时默认分钟、工具箱收起、练习页名、分组模式、自定义预设、聚焦区域、工具箱/放大镜位置）；工具箱与放大镜关闭时保存屏幕坐标，下次打开恢复到工作区内。
- **T6 聚光灯/放大镜联动**：窗口公开 `CurrentScreenRect`；「贴图」把聚焦区截图贴为可编辑卡片（记录来源页），「问 AI」直接以聚焦区发起讲题；截图时临时隐藏遮罩/透镜，避免把遮罩截入。
- **多聚焦区域**：`FocusRegionStore` 保存最多 24 个屏幕区域并按讲解顺序循环切换；聚光灯「保存区 / ◀区 / 区▶」按钮与 `classroom.spotlightRegions` 持久化。

### 架构与安全
- `TeachingSession` 统一记录课件页（含动画步键）、画板页、AI、练习、工具结果与导出；`%APPDATA%\InkCanvas\teaching\session-<id>.json`。
- 动画/点击阶段由 `PptInteropService.GetAnimationStep()`（`SlideShowView.GetClickCount`）读取，写入索引与会话键；不可读时按 0 降级。
- `SlideContext` 作为课件上下文随请求发送，提示词明确「以框选为准」；COM 读取失败降级为纯框选。
- `AiResponseGate` 以会话 ID + 世代丢弃过期响应；关闭浮窗/换题即失效。
- AI 生成内容均先预览/确认；名单、积分、批注、板书、课堂记录全部本地处理。
- P2 仅接口预留：`StudentInteractionContracts`（扫码投票/作答/举手）、`AiClassSummaryContracts`（可编辑摘要草稿 + 来源引用），未实现学生端与摘要生成。

## 9. 验证记录（如实标注）

| 项 | 证据 |
|---|---|
| App 构建 | `build.ps1 build`（整个解决方案）→ 0 警告 0 错误（命令输出） |
| Ai 测试 | 132/132 通过（命令输出） |
| P0 测试 | 33/33 通过（命令输出） |
| Classroom 测试 | 252/252 通过（命令输出） |
| Core 测试 | 148/148 通过，0 跳过（命令输出）；修复编译基线、补齐多笔画识别输入，并对齐旧 Settings 字段（experimental/penNib/highlighterWidth）；逐值差异以 `KnownMigrationDiffs` 精确登记（见下） |
| 实机自检 | `InkCanvas.exe --selftest` → `allCriticalChecksPassed: true`，`presentedFrames: 159`，`strokeCount: 6`，选区拖拽缩放/删除/撤销均通过（selftest.json） |
| 新增 UI 打开 | 实机 UIA 点击浮条「课堂工具」→「分组积分 / 编辑场景 / 倒计时」，三个窗口均打开并截图；图像识别确认预设窗口底部「保存并应用/取消」完整可见、分组窗口显示无名单提示、倒计时显示 00:00 与分钟预设。截图：`artifacts/teaching-loop-ui/`。 |
| E2E：AI 四模式 | UIA 驱动：画一笔 → 框选 → AI 识别；默认不显示公式，切「分步引导」出现「下一步」且逐步揭示，切「完整解答」显示公式卡（E = mc²），切「教师解析」正常。日志与截图：`artifacts/teaching-loop-e2e/e2e-ai2.log`、`e2e2-*.png`。修复了测试暴露的浮窗构造崩溃（`_studyRow` 过早加入布局）与 Hint 会话无法升级到更多内容的问题（复用识别结果重新生成）。 |
| E2E：分组积分贴板 | UIA 在点名窗口导入张三/李四/王五/赵六 → 分组积分读取同一名单（4 人 4 组）→ 第一组 +5 → 显示 5 分 → 「贴到白板」打开画板。日志与截图：`artifacts/teaching-loop-e2e/e2e-group.log`、`e2e-group-*.png`。 |
| E2E：练习闭环 | UIA 驱动：AI 识别后点「基础变式」→ 变式题生成 → 预览对话框「开始练习」→ 画板自动打开并新增「第 2 页 随堂练习」→ 倒计时窗口自动启动（02:57）。日志与截图：`artifacts/teaching-loop-e2e/e2e-practice*.{log,png}`。同时修复了变式按钮被滚动区裁剪与初始 Collapsed 导致不可点的问题。 |
| E2E：聚焦区域 | 打开聚光灯 → 拖动亮区 → 「保存区」写入 `Settings.json.classroom.spotlightRegions`（校验非空）→ 「贴图」后画板窗口存在并截图。日志与截图：`artifacts/teaching-loop-e2e/e2e-spotlight*`。多区域循环切换按钮已渲染，未逐次点击。 |
| E2E：图形标注联动 | 框选→AI 识别→切「分步引导」重新生成后，缩略图上出现与解释步骤对应的蓝色折线标注；「隐藏标注」→折线消失且按钮变「显示标注」→再点恢复。图像识别确认折线存在。日志/截图：`artifacts/teaching-loop-e2e/e2e-annotation.log`、`annotation-{visible,hidden}.png`。修复了锚点设置后不触发标注重绘的真实缺陷。 |
| E2E：讲义导出 | 使用最近课件索引（2 页 + 缩略图 + 教师解析）打开「讲义导出」→预览对话框→点「导出」→生成 `handout-20261121-133200/`，含 `handout-001.png`、`handout-002.png`、`handout.pdf`（头 `%PDF-1.4`，1.6MB）。原索引与缩略图未被修改。日志/截图：`artifacts/teaching-loop-e2e/e2e-handout*`。 |
| E2E：工具贴板与预设保存 | 点名「单次抽取」→「贴到白板」→ 画板窗口出现；抽奖「抽取」→ 自动贴板 → 画板窗口保持；预设编辑器改名「E2E 自定义场景」并勾选画板/倒计时 →「保存并应用」→ `Settings.json.classroom.customPresets` 解析出该名称与工具列表，`presetId` 切到自定义 ID（测试后已恢复为 teach）。日志/截图：`artifacts/teaching-loop-e2e/e2e-tools*`。 |
| E2E：倒计时快照/书签（历史归档） | 当时录制耦合验证：打开画板写一笔（录制）→ 倒计时 1 分钟 → 归零后新增快照 `Screenshot-130918.png`，关闭画板后录制文件含 `timer`/「倒计时结束」书签。日志与截图：`artifacts/teaching-loop-e2e/e2e-timer.log`、`e2e-timer-session.json`、`e2e-timer-*.png`。插件化后书签由插件依据课堂信号生成，该链路以插件验收为准。 |

**Core 旧向量已登记的迁移差异**（`KnownMigrationDiffs`，逐 case+JSON 路径精确豁免；其余字段仍严格断言）：
- 虚/点线分段数（dashed-line、dashed-line-negative、dot-line、dot-line-negative、dashed-circle、dot-circle）；
- 箭头与坐标轴侧翼采样（arrow、coordinate-*）；
- 平行线辅助线数量（parallel-*）；中心矩形/椭圆笔画数（rectangle-from-center、ellipse-from-center、ellipse-foci-*）；
- 圆/椭圆采样起始相位（circle、ellipse）；双曲线/抛物线/圆柱/圆锥/长方体分支与步长（hyperbola-*、parabola-*、cylinder-*、cone-*、cuboid-*）；
- 识别器钩形/回环类别号（reject-hook、reject-back-over）；顶部/右侧手柄拖动矩阵分量（drag-top、drag-right）；零宽笔画边界宽度（bounds-zero-width）。
- `icink` 旧字段（ObjectFrame/Nib/NibRotation/ShapeSource/LayerOrder）C# 端不再导出，比较时忽略；Settings 新增节点按逐字段比较，不因新增 `classroom`/`experimental` 失败。

**未验证（不得视为已达成）**：
- 真实 PowerPoint/WPS 下的 `GetSlideText`、`GetAnimationStep`（动画点击阶段）、`MediaFormat.Pause` 行为（本机以 COM 可读则生效、失败静默降级）。
- 真实 AI 端点的变式题/标注/单区重识别返回格式；验证使用内置 mock 流。
- 讲义在真实课件上的公式/字体/分页精度与批注位置；本次已实机导出 2 页 PNG+PDF 并校验文件结构，但未逐像素核对真实课件排版。
- 图形标注「贴回白板」后独立标注层的拖动/修改/删除点击链路；当前已验证浮窗内高亮与显示/隐藏，贴回层依赖既有 `AiPasteMetadata.Annotations` 与 BoardImageLayer。
- AI 讲解回放、变式题对话框内文本编辑、多聚焦区循环切换、工具箱打开不抢焦点/不误触等其余 UI 点击路径；点名/抽奖/分组/练习结果贴板、预设保存与讲义导出已完成实机 E2E。
- 高 DPI/高对比度、90 分钟压力、多屏、真实投屏模式。
- PPT 插页/删页/调序后的端到端批注归属复核（沿用既有 slideId 迁移机制，未新增回归）。

**剩余简化/边界（非缺失，已实现但能力有限）**：
1. 动画步依赖 COM `GetClickCount`；WPS 或旧版 Office 不支持时按 0 记录，同页不同阶段可能合并。
2. 单区重识别用 mock 文本验证（真实 OCR 未接入）；重识别后会自动携带完整拼题上下文重发求解。
3. 多聚焦区域仅聚光灯支持保存/切换（放大镜保持单次取景）；上限 24 个，超出拒绝并提示。
4. AI 讲解「重新调出」为文本回放（点击导航面板带 AI 标记的页），不重新调用 AI、不重建标注层。
5. 个人预设编辑器支持工具勾选/倒计时/名称，未提供布局与窗口位置级预设。
6. 工具位置仅持久化工具箱与放大镜（聚光灯全屏、倒计时/分组等窗口仍居中打开）。