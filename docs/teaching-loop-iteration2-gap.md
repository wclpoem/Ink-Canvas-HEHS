# 现状审查与迭代清单（第二轮：课堂链路 / 内容复用）

- 审查日期：2026-11-21
- 分支：`完善AI助教、课件联动与课堂练习闭环`
- 审查方式：只读代码检索 + 真实构建/测试输出 + 上一轮实施记录（`docs/teaching-loop-gap.md`）
- 审查基线：构建 0 警告 0 错误；Classroom 269 / Ai 132 / P0 33 / Core 148 = 582 通过
- 结论：AI 讲题、课件导航、随堂练习、讲义导出、工具箱协同已具备；**完整课堂链路回归、Feature Flag、P2 例题库与课堂资源包**为真实缺口。

## 1. 已完成能力与可复用模块

| 能力 | 可复用模块 | 证据 |
| --- | --- | --- |
| AI 四模式讲题 | `AssistantTeachingPolicy`、`AssistantTeachingMode`、`AssistantWindow` | 上一轮 E2E 与实际 UI 截图 |
| 跨选区拼题 | `ProblemContext`、`ProblemContextPolicy` | Ai 132 测试 |
| 图形标注联动 | `AnnotationLayerPolicy`、`AnnotationLayer` | 上一轮 E2E 折线显示/隐藏 |
| 课件导航 | `SlideContentIndex`、`SlideNavigatorWindow` | 上一轮 2 页索引与截图 |
| 随堂练习闭环 | `ExerciseFlowPolicy`、`ExerciseFlow`、倒计时书签 | 上一轮 E2E「开始练习」 |
| 讲义导出 | `HandoutComposer`、`HandoutExportService`、`HandoutPdfBuilder` | 上一轮 2 页 PNG + `%PDF-1.4` |
| 工具箱协同 | `ClassroomPreset`、`ClassroomPresetLibrary`、`GroupScoreService`、`FocusRegionStore` | 上一轮 E2E |
| 统一课堂会话 | `TeachingSession` | Classroom 测试 |
| AI 过期响应治理 | `AiResponseGate` | Ai 测试 |
| 时光机（历史归档） | 原 `InkTimeline`、`ClassroomSessionStore`、`SessionJournal` | 本轮 269 / 582 通过；录制链路已重构为独立插件 `inkcanvas.time-machine`，时间轴 / 导出用例迁至插件测试项目，恢复 / 校验 / 落盘链路删除 |

## 2. 真实缺口

| ID | 缺口 | 影响 | 复用点 | 优先级 | 验收方法 |
| --- | --- | --- | --- | --- | --- |
| C1 | 无「完整课堂链路」单一自动化回归（打开课件→放映→批注→AI 讲题→贴回→新练习页→倒计时→快照→回放→导出） | 每次发版仍靠分散 UIA 脚本，无法证明链路无串题/串页/串会话 | `TeachingSession`、`ExerciseFlowPolicy`、插件录制时间轴、`HandoutExporter` | P0 | 新增纯逻辑链路用例，断言每一步数据归属与最终导出一致 |
| C2 | 无 Feature Flag 配置层，新行为无法一键回退 | 违反工程约束「可配置回退」 | `AppSettings` | P0 | 新增 `features` 节点与读取服务，关闭时走原逻辑；单测覆盖 |
| C3 | 无 AI 首字延迟测量 | 交付物 4 缺对比数据 | `AiClient` mock / 流式事件 | P1 | mock 流首字节到达时间统计，输出毫秒 |
| C4 | 无本地例题库（按课题/知识点/来源课件/日期检索） | 课后无法复用 AI 讲题结果 | `TeachingSession` 的 AI 记录、`SlideContentIndex` | P2 | 服务层 + 本地 JSON + 检索单测 |
| C5 | 无课堂资源包归档/重开（课件+批注+练习+回放） | 历史内容无法整体迁移 | 课件批注存储（`PptInkFileStore`）、`ExerciseFlow`；课堂录制文件属插件私有目录，资源包不再枚举会话存储 | P2 | zip 打包/解包往返测试，校验不覆盖原文件 |
| C6 | 90 分钟「连续书写」帧率未实测 | 仅有合成回放/落盘压测 | 插件录制器 | P0（环境受限） | 真实课堂素材；当前标注未验证；长课压测归属插件测试 |
| C7 | WPS 放映与多版本 PPT 兼容矩阵未实测 | 仅 PowerPoint 16.0 联调 | `PptInkSession`、`PptInteropService` | P0（环境受限） | 需 WPS/多版本 Office 实机；当前标注未验证 |

## 3. 性能瓶颈与缺失测试

| 项 | 现状 | 本轮动作 |
| --- | --- | --- |
| 长课回放 | 162,001 帧 106.96s、内存 55.6→57.1MB（历史归档：时光机时间轴压测，随插件迁移复核） | 已达标，无需再改 |
| 长课录制落盘 | 54,000 事件 191ms（历史归档：journal / 会话落盘与恢复链路已删除，不迁入插件） | 不再作为本体指标 |
| AI 首字延迟 | 无数据 | 新增 mock 统计测试 |
| 完整链路耗时 | 无数据 | 链路用例记录总耗时与每步结果 |
| 快速连续翻页 | 有 slideId 迁移测试，无「快速连续」压力 | 链路用例中加入连续翻页断言 |
| 动画/视频状态 | COM 读取失败降级为 0 | 保留，标注未实机 |

## 4. 本轮可完成 vs 应延期

| 项 | 本轮 | 理由 |
| --- | --- | --- |
| C1 完整课堂链路回归 | 完成 | 纯逻辑串联，可自动化 |
| C2 Feature Flag | 完成 | 配置 + 默认开启 + 关闭回退 |
| C3 AI 首字延迟 | 完成（mock） | 有流式 mock，可测量 |
| C4 例题库 | 完成（服务层） | 本地 JSON + 检索；UI 接入延期 |
| C5 课堂资源包 | 完成（服务层） | zip 往返；UI 接入延期 |
| C6 真实 90 分钟书写帧率 | 延期 | 需真实课堂素材 |
| C7 WPS / 多版本兼容 | 延期 | 本机未安装 WPS，仅 PowerPoint 16.0 |
| 多屏 / 系统级 200% DPI | 延期 | 本机单屏、需注销 |

## 5. 实施计划（对应需求十一节）

- **第一批**：C1 完整课堂链路用例、C2 Feature Flag、C3 首字延迟；回归 582 项。
- **第二批**：已实现（AI 分步/局部纠错、批注导航、随堂练习），本轮补链路覆盖与回归。
- **第三批**：C4 例题库、C5 课堂资源包（服务层）；UI 接入延期。

## 6. 不得破坏

- 既有白板书写、撤销/重做、批注归属与画板历史文件；时光机录制格式属独立插件，不再纳入本体兼容约束。
- 所有新增数据只追加字段/新增文件，不改变既有 JSON 语义。
- 所有生成内容需教师确认；本地处理，不上传。
## 7. 本轮实施结果（2026-11-21）

| 缺口 | 状态 | 落地位置 | 验证 |
| --- | --- | --- | --- |
| C1 完整课堂链路回归 | 已完成（历史记录） | `tests/InkCanvas.Classroom.Tests/ClassroomChainRegressionTests.cs` | 单一用例串起 10 步：课件页→批注→分步讲题→过期响应→贴回→练习页→倒计时书签→四路回放→讲义选页/导出采样→最终一致性；100ms 通过。插件化后其中录制 / 时间轴 / 导出段随链路用例拆分，归插件测试复核 |
| C2 Feature Flag | 已完成（模型层） | `AppSettings.FeatureSettings` + `FeatureGate` + `FeatureFlagTests` | 6 项通过；旧 JSON 缺键默认全开；关闭项返回 false |
| C3 AI 首字延迟 | 已完成（mock） | `tests/InkCanvas.Ai.Tests/FirstTokenLatencyTests.cs` | 10 次：均值 63.0ms、最大 66ms、最小 62ms（本地 mock 流，非真实端点） |
| C4 本地例题库 | 已完成（服务层 + 窗口 UI） | `src/InkCanvas.Core/Classroom/ExampleLibrary.cs`、`src/InkCanvas.App/Views/ExampleLibraryWindow.cs`、工具箱「例题库」入口、`ShellController.OpenExampleLibrary` | 6 项服务测试 + 3 项静态契约测试；实机打开窗口截图 `artifacts/example-library-window.png` |
| C5 课堂资源包 | 已完成（服务层 + 窗口 UI） | `src/InkCanvas.Features/Classroom/ClassroomResourceBundle.cs`、`src/InkCanvas.App/Views/ResourceBundleWindow.cs`、工具箱「资源包」入口、`ShellController.OpenResourceBundle` | 5 项服务测试 + 4 项静态契约；实机窗口截图 `artifacts/resource-bundle-window.png`。插件化后资源包不再枚举时光机会话存储，录制文件属插件私有目录 |
| C6 真实 90 分钟书写帧率 | 延期 | — | 需真实课堂素材 |
| C7 课件变异兼容 | 部分完成 | 新增 `tests/InkCanvas.P0.Tests/SlideMutationAttributionTests.cs`（真实 PowerPoint COM：插页/删页/调序）+ `PptInkFileStore` 旧目录回填/合并修复 | 调序后按 slideId 完整不串页；插页新页 0 批注；删页旧批注保留并被 `MissingSlideIds` 标记；页数变化时旧目录文件自动复制到新目录；快速连续翻页 50 次/6.3s 应用存活且无新增异常；WPS/多版本仍延期 |

本轮回归（真实命令输出）：构建 0 警告 0 错误；**Classroom 297 / Ai 134 / P0 45 / Core 148 = 624 通过**。
第二批专项回归：Ai 教学模式/局部纠错/拼题 54 项、Classroom 练习/导航/讲义/预设/分组 19 项，全部通过。
实机：例题库窗口可从「课堂工具 → 例题库」打开（`artifacts/example-library-window.png`），显示检索框、暂无例题提示与关闭按钮。

### Feature Flag 配置说明

`Settings.json` 新增 `features` 节点，默认全部开启：

```json
"features": {
  "enableAiTeaching": true,
  "enableExerciseFlow": true,
  "enableExampleLibrary": true,
  "enableResourceBundle": true
}
```

| 开关 | 关闭后的行为 |
| --- | --- |
| `enableAiTeaching` | 预留：AI 讲题路径可回退（本轮仅提供读取入口） |
| `enableExerciseFlow` | 预留：练习闭环可回退（同上） |
| `enableExampleLibrary` | 例题库 `Add/Delete` 返回 false、`Search` 返回空，`Load` 仍可读 |
| `enableResourceBundle` | 资源包 `Archive` 抛 `InvalidOperationException("课堂资源包已关闭")`，`Restore` 仍可读 |

旧配置文件缺少 `features` 键时按全开处理，不需要迁移。

### 本轮如实标注的未验证项

1. 例题库与「收藏例题」已完成三类来源实机核对：无白板 `boardPage=-1`、白板第 1 页 `boardPage=0`、PPT 放映 `sourcePresentation=p0-test.pptx` 且 `sourceSlideId=256`；样本留存 `artifacts/examples-with-sources.json`。例题库窗口可读回条目（标题/课题/知识点/来源/日期）。
2. 资源包已接入窗口 UI（打包课件 / 批注 / 练习等课堂文件 / 解包到 `bundles\<时间戳>`），实机可打开；时光机录制文件属插件私有目录，不纳入资源包枚举；真实 PPT/批注大文件与中文长路径未验证。
3. `FeatureGate` 已对例题库/资源包服务、AI 收藏按钮、AI 讲题发送/追问、练习变式按钮生效（默认全开时行为不变）。
4. C1 链路用例中的倒计时、快照、导出为纯逻辑等价模型，未启动真实 UI/Win2D/COM/Media Foundation；录制与时间轴段归属插件测试。
5. 真实 AI 端点首字延迟、真实课件打包（大文件/中文长路径）、并发写入未验证。
6. 90 分钟真实书写帧率、WPS、多屏、系统级 200% DPI 仍未验证（外部环境依赖）。
## 8. 收尾结论（2026-11-21，经用户确认）

- **内部可执行项已全部完成**：审查清单、Feature Flag、完整课堂链路回归、真实 COM 插页/删页/调序与快速翻页、回填/manifest 合并修复、例题库/资源包 UI、AI 收藏、真实课件讲义导出；构建 0 警告 0 错误，**Classroom 297 / Ai 134 / P0 45 / Core 148 = 624 通过**。
- **外部条件未验证项（固定标注，不再继续）**：
  1. 真实课堂素材的 90 分钟连续书写/翻页/录制帧率与内存曲线（现有为合成逐帧 162,001 帧 / 106.96s / 内存 55.6→57.1MB，不代表真实课堂；长课压测归属「时光机」插件测试）；
  2. 真实 AI 端点首字延迟与识别/纠错成功率（现有为 mock 63ms；原合成音轨证据随讲解音频链路移除）；
  3. WPS 放映链路（本机未安装 WPS，仅有 PowerPoint 16.0）；
  4. 多显示器组合（本机单屏 1536×864）；
  5. 系统级 200% DPI（Win11 需注销重登，未执行；注册表已恢复原值）；
  6. 真实课件讲义排版精度与 PDF 像素级正确性（现有为真实 PPTX 渲染 + 应用合成，页数与顺序已校验）。
- **结论**：本迭代**不声明为目标完全达成**；上述 6 项需在具备真实素材/软件/硬件/可注销的环境后单独补证。
- 工作区改动未提交、未推送。