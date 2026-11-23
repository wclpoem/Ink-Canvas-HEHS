# AI 助手插件

AI 助手是面向教师的通用对话侧栏，可协助备课、教学设计、课堂活动、评价反馈、学科讨论和日常事务。通过原生插件 `inkcanvas.ai-assistant` 提供，宿主使用 `IAiAssistantService` 打开侧栏。

## 对话与界面

打开助手进入空白对话，输入任务后生成回复。可主动添加当前白板页面、选区截图、图片和本地文件。支持 Enter 发送、Shift+Enter 换行和中文输入法。每条回复可复制，生成期间可停止。

顶部可以新建和切换对话，管理菜单支持重命名、删除、左右停靠和收起。窗口使用项目的 `WindowFrame`、`TouchIconButton`、`MarkdownView` 和主题资源，支持深浅色主题。

消息采用 `ListView` 的容器回收，仅创建可见区域附近的消息控件。图片预览按需解码，关闭预览后释放图像。流式回复每 50 毫秒合并更新，复用已完成的 Markdown 块。向上阅读时保持位置，处于底部时跟随新内容。

## API 连接与思考

在“设置 → 插件设置 → AI 助手”中添加连接、选择模型并保存。支持 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 和 Google Gemini 协议，保留供应商预设、模型列表、密钥凭据管理和联网搜索设置。

输入区的思考设置提供开关和低、高、最高三档。新对话默认开启思考并使用高档。连接设置可自动识别模型是否支持思考参数，也可手动指定支持或不支持。请求按协议映射到推理强度、思考预算或思考等级，实际可用等级由模型决定。接口拒绝扩展参数时会回退为文字请求，并在本轮显示回退说明。[OpenAI 推理](https://developers.openai.com/api/docs/guides/reasoning) · [Anthropic 思考](https://platform.claude.com/docs/en/build-with-claude/extended-thinking)

## 附件

图片最长边限制为 2048 像素，使用质量 80% 的 JPEG 保存到对话中，预览与请求复用压缩结果。普通文件仅保存本地绝对路径，每次需要时重新读取。文件移动或删除后会显示读取失败，可重新添加。

文本、Markdown、CSV、JSON、XML、HTML、常见代码和 LaTeX 文件会提取文字。DOCX、PPTX、XLSX 和 ODT 提取正文或表格文字。PDF 根据模型协议发送原始文件，连接设置可关闭原生 PDF 输入。无法读取的格式会保留路径并说明需要转换。接口拒绝多模态请求时自动尝试文字回退，可提取的附件文字继续提供给模型。

## Agent 与工具

Agent 循环内置在 AI 插件中，自动在模型请求、工具执行和结果继续推理之间运行。“助手与工具调用”设置中的“允许工具调用”默认开启，关闭后停止当前任务，后续请求只进行对话。

宿主的 `IAssistantToolRegistry` 提供画布、页面、课件和课堂工具。内置工具支持读取工作区、查看页面截图、创建可编辑路径、调整或删除选区、撤销与重做、创建和管理白板页面、打开课堂工具，以及调用实际安装的插件命令。其他原生模块可以通过注册表增加自己的工具。每次执行都在 UI 线程检查取消状态，页面切换与停止生成会取消旧任务。

原生协议保留一轮工具调用所需的调用 ID、参数和结果。Responses 在工具循环中携带加密推理项，Anthropic 与 Gemini 保留签名和原始内容。工具记录随对话保存，含已执行工具的回合不提供自动重试，以防重复修改。

## 上下文与白板存储

插件设置可以填写模型的上下文窗口并补充教师偏好。请求按估计 Token 数预留输出空间，较早的完整回合压缩为包含目标、约束、事实、已完成操作和待办的摘要，保留最近的完整交流。压缩结果只替换模型输入中的历史部分，完整消息继续保留在白板文件。当前材料过大时提示缩小附件或调整窗口配置。

默认窗口统一为 256k，即 262144 Token。设置支持调整窗口大小和自动压缩触发比例，默认达到预留输出后的 80% 时整理历史。旧版默认 32768 的配置会升级到新默认值，其他自定义窗口保留。压缩分批处理长材料，并在全部批次成功后更新摘要，失败或取消时保留原历史。

Agent 工具循环也会自动压缩。每组完整的工具调用与结果一起处理，最近一组的原生调用信息和推理签名继续保留。较早操作的摘要记录参数、实际结果、失败和待办，完整工具结果存入回合记录。`assistant_history_read` 可分页找回压缩前的消息、结果和附件。

原生插件分别提供自己的工具和当前上下文。工具声明较多时优先提供相关能力，通过 `tool_catalog` 查找并加入其他工具，减少大目录占用上下文。MCP 的配置和插件接口见 [MCP 与插件工具适配](MCP.md)。

同一 `.icink` 文件保存全部对话、当前对话、草稿、思考设置、上下文摘要、工具结果和压缩图片。数据位于 ZIP 容器的 `extensions/inkcanvas.ai-assistant.bin`，由插件解释。普通白板读取器继续读取 `board.json`。宿主在未启用 AI 插件时保留扩展数据，界面不提供对话读取入口。

对话变更参与白板自动存档的脏状态，手动保存前刷新对话快照。关闭侧栏再打开恢复当前白板的对话，打开其他白板后切换到对应记录。文件附件正文与 API Key 不写入对话存储。

## 构建与分包

```powershell
dotnet publish src/InkCanvas.App/InkCanvas.App.csproj -c Release -p:Platform=x64 -o artifacts/app-with-ai
dotnet publish src/InkCanvas.App/InkCanvas.App.csproj -c Release -p:Platform=x64 -p:IncludeAiAssistantModule=false -o artifacts/app-without-ai
.\tools\pack-extension.ps1 -Project src\InkCanvas.Modules.AiAssistant\InkCanvas.Modules.AiAssistant.csproj -Kind Module
```

基础包使用全新输出目录。`IncludeAiAssistantModule=false` 排除 AI 插件的构建与复制，现有 AI 卡片仍可通过通用图片对象与兼容元数据打开、选择和编辑。

## 存档命名与描述

在“设置 → 保存与备份 → AI 存档命名与描述”中填写支持多图输入的模型接口完整地址和模型名称，保存配置后打开“保存后自动分析并命名”。默认关闭。文稿库中也可对选中的文件点击“AI 命名与描述”执行一次处理。

OpenAI 兼容模式使用 Chat Completions 多图格式，地址应包含 `/v1/chat/completions`。密钥保存在 Windows 凭据管理器中，可使用 `AI_API_KEY` 或 `OPENAI_API_KEY` 环境变量。选择现有 Ink Canvas 助教协议时，服务收到 `purpose=archive-caption`、多张 `images` 和 JSON 输出要求。接口留空时优先沿用“AI 助手”中当前连接、模型和凭据，未选择连接时沿用 `AI_ENDPOINT`、`AI_MODEL` 和助教协议。存档命名要求真实配置的模型服务。相关多图协议见 [官方文档](https://developers.openai.com/api/docs/guides/images-vision?api-mode=chat)。

保存完成后，插件在后台从磁盘快照渲染，逐页读取图片和视频封面，跳过视频主体数据。单张 PNG 最长边限制为 3840 像素，总像素预算约 829 万。范围在预算内的屏幕截图与墨迹保持原像素尺寸。超过范围的批注生成等比缩小的总览，并附带最多两张墨迹密集位置的高清局部，缩小总览适当增强细线可见性。

白板存档的所有页面按顺序导出。长页或宽页按页面可视尺寸分段，相邻分段重叠 64 像素，跳过不含内容的空段，保留负坐标墨迹。每次最多发送 4 张、约 32 MB 的 PNG，超过后分批分析并传递前批摘要，最终生成整个文件的标题和描述。单个长页最多 1024 个分段，超出时提示拆分文稿。

`.icink` 的标题与描述写入 `archive.json`，实际文件按生成的标题重命名，重名时添加序号。原始笔画、笔锋、媒体和纸型仍可编辑。写回前检查保存时的大小和修改时间，文件在分析期间发生变化时跳过旧结果。旧 JSON 文稿成功命名时迁移为当前 ZIP 容器。

PDF 和解析几何插件通过适配接口提供各自原生渲染的 PNG，保存后的 AI 标题建议与描述写入相邻的 `.ai.json` 文件。它们的编辑窗口继续使用已打开的文件路径。文稿库会同步白板新文件名、最近使用与收藏，并在预览区显示描述。资源管理器继续使用存档内嵌封面。

自动处理采用串行队列，正常存档保存先完成。退出前尚未处理的原生白板路径会记录到插件私有队列，下次启动且功能开启时恢复。模型失败时原文件保留，可手动重试。关闭开关会取消当前自动任务并清空自动队列。

PNG 与分析结果存放在应用设置目录的 `PluginData/inkcanvas.ai-assistant/Documents/Images`。历史缓存上限约 256 MB，当前任务已完成分析的 PNG 超过约 128 MB 时优先回收较早图片，分析清单仍记录其页码与范围类型。

## 插件适配接口

`IAiDocumentService` 由 AI 插件实现和导出，提供存档分析与设置视图。`IAiDocumentBridge` 由宿主提供给 PDF、解析几何和文稿库，动态转发到已加载的 AI 插件。

`AiDocumentRequest` 包含源文件路径、来源插件 ID 和可选的 `ExportImages` 委托。自定义来源按需实现 `IAsyncEnumerable<AiDocumentImage>`，将 PNG 写到分配的目录，并提供原始页码、图像角色和像素尺寸。原生白板直接提供 `.icink` 路径，由 AI 插件负责渲染。调用 `Saved` 可参与保存后自动分析，调用 `DescribeAsync` 可主动执行一次。

图像角色可以使用 `page`、`segment`、`overview`、`detail` 或来源插件自己的标记。结果包含原路径、新路径、标题、描述和图片数。适配层通过 Host.Contracts 共享契约，调用方无须引用 AI 插件 DLL。此版本接口需要随附的新宿主构建。

## 验证

`AssistantAgentTests` 覆盖四种协议的原生工具调用、思考签名和附件参数，以及摘要边界与会话序列化。`BoardExtensionTests` 覆盖宿主保留插件数据和白板、单页保存恢复。`--aiplugincheck=<目录>` 验证实际侧栏、附件、工具循环、回退、取消、多会话、自动摘要、虚拟列表与插件停用。`--aiconnectionscheck=<目录>` 验证四种协议的配置和实际请求流程。

`DocumentNamingTests` 覆盖原像素导出规划、大幅批注局部图、长页覆盖、文件名清理及多图 HTTP 请求。`ArchiveDescriptionTests` 覆盖无损写回、同名避让、过期结果拒绝、旧版迁移和跳过视频主体。`--documentaicheck=<目录>` 使用本地模拟视觉模型执行本机 WinUI、Win2D 与保存流程验收，结果写入 `document-ai.json`。
