# 本轮交付收尾：文件分组、提交建议与截图索引

> **【历史归档 · 2026-11-21 交付记录】** 本文件记录的是时光机尚在本体内置阶段的当轮收尾。时光机已重构为独立插件模块「时光机」（Id `inkcanvas.time-machine`），下文 B / C / D 三组列出的本体源码与测试条目已随重构删除、迁移至插件测试项目或失效；恢复、校验、备份、迁移与讲解音频相关内容不再是现行功能。现行形态见 `docs/timemachine-user-guide.md`，本文件正文仅作历史留档，不代表当前代码结构。

> 用途：交接与后续提交参考。**本文件不执行任何 git 操作**；工作区改动仍未提交。
> 验收结论与未验证项见 `docs/timemachine-reliability-v2.md`、`docs/timemachine-test-evidence.md`（均为同期历史归档）。

## 1. 本轮涉及文件（建议按语义分 4 组提交）

### A. AI 图标统一与主题适配

| 文件 | 性质 |
| --- | --- |
| `src/InkCanvas.Controls/Controls/AiGlyph.cs` | 新增：AI 标记唯一资源层（16/20/24 档位、固定度量盒） |
| `src/InkCanvas.Controls/Controls/ThemeBrushes.cs` | 新增：主题令牌解析（显式主题 + 系统高对比度） |
| `src/InkCanvas.Controls/Themes/Tokens.xaml` | 修改：主题字典画刷改字面量；新增 HighContrast 令牌 |
| `src/InkCanvas.Controls/Controls/TouchIconButton.cs` | 修改：改用 AiGlyph / ThemeBrushes |
| `src/InkCanvas.Controls/Controls/FloatingBar.cs` | 修改：显式主题传递、分隔线与外框主题化 |
| `src/InkCanvas.App/Views/FloatingBarWindow.cs` | 修改：`ApplyTheme` + 200% 缩放按工作区降级（渲染上限 150%） |
| `src/InkCanvas.App/Views/ToolkitWindow.cs` | 修改：AI 磁贴改用 AiGlyph |
| `src/InkCanvas.App/Views/AssistantWindow.cs` | 修改：标题徽标改用 AiGlyph |
| `src/InkCanvas.App/Views/SlideNavigatorWindow.cs` | 修改：课件页 AI 角标改用 AiGlyph（该文件本身为既有未跟踪文件） |
| `src/InkCanvas.Core/Settings/AppSettings.cs` | 修改：新增 `ai.hasSeenLassoHint` |
| `src/InkCanvas.App/Services/ShellController.cs` | 修改：首次套索引导、浮条显式主题调用 |
| `tests/InkCanvas.Classroom.Tests/AiGlyphResourceTests.cs` | 新增：图标资源引用回归 |

建议提交信息：`refactor(ui): 统一 AI 助教图标资源层并修复暗色/高对比度/高缩放主题适配`

### B. 时光机 P0 数据可靠性（历史归档：对应源码与测试已随插件化删除 / 迁移）

| 文件 | 性质 |
| --- | --- |
| `src/InkCanvas.Core/Classroom/TimelineEvent.cs` | 修改：SessionId / Seq / PptSlideId / AiStep |
| `src/InkCanvas.Core/Classroom/InkTimeline.cs` | 修改：稳定排序、检查点 Seq、笔画按需解析 + 4096 缓存上限 |
| `src/InkCanvas.Features/Classroom/InkRecorder.cs` | 修改：统一分配 SessionId/Seq、PPT 归属 |
| `src/InkCanvas.Features/Classroom/ClassroomSessionStore.cs` | 修改：`.sha256` 校验、原子写、旧版 `.bak` 迁移 |
| `tests/InkCanvas.Classroom.Tests/TimelineReliabilityTests.cs` | 新增：序号/排序/校验/迁移/只读/定位 |

建议提交信息：`feat(timemachine): 事件稳定序号与完整性校验，旧版会话可备份迁移`

### C. 时光机 P1/P2 体验与导出（历史归档：交互窗口与导出器已迁入插件）

| 文件 | 性质 |
| --- | --- |
| `src/InkCanvas.App/Views/TimeMachineWindow.cs` | 修改：键盘快捷键、时间轴 1×/2×/4× 缩放、关键刻度、按章节片段、光标/章节选项、隐私提示 |
| `src/InkCanvas.Features/Classroom/TimeMachineExporter.cs` | 修改：ShowCursor / ShowChapterInfo 叠加 |

建议提交信息：`feat(timemachine): 时间轴缩放与键盘操作，导出支持章节片段/光标/章节信息`

### D. 压测、证据与文档（历史归档：落盘 / 恢复压测用例已删除，回放与导出用例迁至插件测试项目）

| 文件 | 性质 |
| --- | --- |
| `tests/InkCanvas.Classroom.Tests/LongPlaybackStressTests.cs` | 新增：90 分钟 × 30fps 逐帧回放压测 |
| `tests/InkCanvas.Classroom.Tests/RecordingThroughputTests.cs` | 新增：90 分钟录制落盘与恢复 |
| `tests/InkCanvas.Classroom.Tests/ExportTimingTests.cs` | 新增：真实 MP4 导出耗时 |
| `docs/timemachine-gap-audit.md` | 新增：现状与差距清单 |
| `docs/timemachine-reliability-v2.md` | 新增：实施结果与未验证边界 |
| `docs/timemachine-test-evidence.md` | 新增：按验收条款的用例证据 |
| `docs/timemachine-user-guide.md` | 新增：用户操作说明 |
| `CHANGELOG.md` | 修改：本轮条目 |

建议提交信息：`docs(timemachine): 补充可靠性升级的差距清单、验收证据与操作说明`

## 2. 提交前注意事项

1. **不要整仓 `git add .`**：工作区还包含上一阶段「教学闭环」的 16 个已修改文件与 28 个未跟踪文件（如 `TeachingSession.cs`、`GroupScoreWindow.cs`、`AiResponseGate.cs` 等），与本轮无关。
2. `src/InkCanvas.App/Views/SlideNavigatorWindow.cs` 在本轮之前就已是未跟踪文件，提交 A 组时按需决定是否一并纳入。
3. 提交前建议重跑：构建 + 四项目测试（见第 4 节）。
4. 未提交、未推送是当前约定状态；需要提交时请明确指示。

## 3. 实机截图索引（最终留存，`D:\hw\artifacts\`）

| 场景 | 文件 |
| --- | --- |
| 主浮条（亮色） | `ai-icon-v2-bar.png` |
| AI 按钮默认 / 悬停 | `ai-icon-v2-button.png`、`ai-icon-v2-button-hover.png` |
| 次级菜单 AI / 帮助 | `ai-icon-v2-secondary.png`、`help-icon-v2-secondary.png` |
| 课堂工具箱 / AI 磁贴 | `ai-icon-v2-toolkit.png`、`ai-icon-v2-toolkit-tile.png` |
| AI 浮窗（亮 / 暗） | `ai-window-v2.png`、`ai-window-v2-dark.png` |
| 暗色主浮条（修复后） | `ai-icon-v2-bar-dark4.png`、`ai-icon-v2-button-dark4.png` |
| 亮色主浮条（回归） | `ai-icon-v2-bar-light-final2.png` |
| 高对比度 | `ai-icon-v2-bar-highcontrast.png` |
| 125% 缩放 | `ai-icon-v2-bar-scale125.png`、`ai-icon-v2-button-scale125.png` |
| 200% 缩放（降级 150% 后完整可见） | `ai-icon-v2-fullscreen-scale200-fixed.png` |
| 真实 PPT 放映联调 | `ppt-slideshow-overlay.png` |
| 测试原始输出 | `test-evidence-raw.txt`、`export-sync-raw.txt` |

失败或中间尝试的截图已移到 `artifacts/archive-ai-icon-v2/`（未删除，可回溯）。

## 4. 验证命令（提交前复跑）

```bash
cd D:/hw/Ink-Canvas-HEHS
export DOTNET_ROOT="D:/dotnet"; export PATH="/d/dotnet:$PATH"; export NUGET_PACKAGES="D:/dev-cache/nuget"
dotnet build src/InkCanvas.App/InkCanvas.App.csproj -c Debug -p:Platform=x64
dotnet test tests/InkCanvas.Classroom.Tests/InkCanvas.Classroom.Tests.csproj -c Debug -p:Platform=x64
dotnet test tests/InkCanvas.Ai.Tests/InkCanvas.Ai.Tests.csproj -c Debug -p:Platform=x64
dotnet test tests/InkCanvas.P0.Tests/InkCanvas.P0.Tests.csproj -c Debug -p:Platform=x64
dotnet test tests/InkCanvas.Core.Tests/InkCanvas.Core.Tests.csproj -c Debug -p:Platform=x64
```

预期：构建 0 警告 0 错误；269 / 132 / 33 / 148 全部通过（Classroom 含约 1m50s 的两个压测）。

## 5. 已知边界（与本轮收尾无关，仅备忘）

- 主浮条以外的部分窗口（如 `BoardWindow`、`SettingsWindow`）仍在代码中直接索引 `Application.Current.Resources`，其暗色表现未在本轮整改范围。
- 200% 窗口缩放在空间不足时按 150% 实际渲染，用户设置值保留；系统级 200% DPI 未验证。
- WPS 未安装、本机单屏、无真实课堂素材，相关验收项已登记为未验证。