# MCP 与插件工具适配

`inkcanvas.mcp` 是独立原生插件，使用官方 C# SDK 2.4.0 连接 MCP 服务。它通过宿主的工具注册表接入应用，原生插件和 MCP 插件均可在 AI 助手未安装时运行。[官方 SDK](https://github.com/modelcontextprotocol/csharp-sdk)

## 连接外部服务

在“设置 → 插件设置 → MCP 工具连接”编辑 JSON，然后点击“保存并连接”。使用常见的 `mcpServers` 配置结构。

```json
{
  "mcpServers": {
    "teaching-resources": {
      "url": "https://example.com/mcp",
      "transport": "http",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN"
      }
    },
    "local-resources": {
      "command": "python",
      "args": ["D:/Teaching/server.py"],
      "env": {},
      "timeoutSeconds": 120
    }
  }
}
```

每个服务使用 `url` 或 `command` 中的一项。`transport` 可设为 `auto`、`stdio`、`http` 或 `sse`，默认自动选择。HTTP 使用 Streamable HTTP，SSE 用于兼容原有服务。SDK 负责协议版本协商、消息传输和请求取消。配置中的 `enabled` 可停用单个连接。

工具名称使用服务名称和原始工具名称生成稳定标识，多个服务的同名工具可以并存。支持文字、结构化结果、图片、资源链接，以及资源和提示词读取。工具较多时通过 `tool_catalog` 按需查找。服务断开后注销工具，点击“重新连接并刷新工具”可恢复连接和更新能力列表。支持工具列表变化通知的服务会触发重新连接与刷新。

配置保存在用户设置目录 `PluginData/inkcanvas.mcp/connections.dat`，使用当前 Windows 用户的数据保护加密，包含请求头和环境变量。配置编辑器显示解密后的内容。工具与资源查询的结果会作为参考数据交给模型。

## 向外部客户端提供应用工具

开启“向外部 MCP 客户端提供应用工具”，点击“复制外部客户端的 stdio 配置”，粘贴到客户端配置中。复制结果包含 `InkCanvas.McpHost.exe` 的实际路径和当前应用进程的连接名。

`InkCanvas.McpHost` 将标准输入输出连接到当前 Windows 用户的本地管道。应用需要保持运行，重新启动后使用新的客户端配置。MCP 的 `tools/list` 和 `tools/call` 使用原生插件注册的工具，`resources/list` 和 `resources/read` 提供插件当前上下文。工具执行和原生窗口操作调度到应用 UI 线程。

应用导出的目录包含宿主和原生插件工具。外接 MCP 服务的工具通过各自连接使用，以避免本地 MCP 服务反复导入自身能力。

## 原生插件兼容层

每个插件在自己的适配器文件中注册工具和上下文，AI 助手通过通用契约调用。

| 插件 | 工具能力 |
| --- | --- |
| PDF | 打开文件、读页文字、搜索、跳页、截图和保存 |
| 解析几何 | 原生符号计算、读取构造、执行构造指令和插入可编辑构造 |
| 文稿库 | 文件搜索和分页列表、存档信息与预览、打开白板 |
| 板书时光机 | 录制列表、开始与停止录制、读取时间线摘要 |
| 屏幕内容跟随 | 当前设置与运行状态、重新绑定批注位置 |
| 云同步 | 同步状态、按当前用户设置执行同步 |

适配器使用 `AssistantPlugin.Tool` 和 `AssistantPlugin.Context`，不引用 AI 助手的实现程序集。工具声明包含所属插件、只读标记和是否切换目标。注册对象随模块生命周期释放，已注销工具不可继续调用，正在执行的操作收到取消令牌。

外部命令插件继续通过宿主已有的 `invoke_plugin_command` 接入，保留原有进程边界与画布结果提交路径。旧版文稿导入插件使用已有文件导入流程。

## 验证

`McpCompatibilityTests` 使用真实 MCP 连接验证工具、资源、图片、插件注销和配置兼容。应用参数 `--assistantcompatcheck=<目录>` 验证原生插件工具、PDF 读页、MCP stdio 双向调用、HTTP 协商与认证。加上 `--without-ai` 可验证 AI 插件停用时的独立运行。
