using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class AiPluginRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var server = new FixtureServer();
        string settingsDirectory = Path.Combine(directory, "settings");
        string connectionDirectory = Path.Combine(settingsDirectory, "PluginData", "inkcanvas.ai-assistant", "Documents", "Connections");
        Directory.CreateDirectory(connectionDirectory);
        string id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(connectionDirectory, "connections.json"), JsonSerializer.Serialize(new
        { ActiveId = id, Connections = new[] { new { Id = id, Name = "本机验收", BaseUrl = server.Address, Model = "fixture", Format = "ChatCompletions", Reasoning = "unsupported" } } }));
        using var services = new AppServices(settingsDirectory);
        services.Settings.Startup.IsAutoUpdate = false; services.Settings.PowerPoint.PowerPointSupport = false;
        bool disabled = Environment.GetCommandLineArgs().Contains("--without-ai");
        if (disabled) services.ExtensionPreferences.DisabledModules.Add("inkcanvas.ai-assistant");
        using var shell = new ShellController(services); shell.Start(); shell.StartInClickThrough();
        try
        {
            var assistant = services.Modules.GetExport<IAiAssistantService>();
            Check("optional plugin availability", (assistant is not null) == !disabled);
            shell.OpenAiAssistant();
            if (disabled)
            {
                Check("disabled plugin opens no conversation window", !shell.OpenWindows.Any(window => window.GetType().Name == "AssistantWindow"));
                Check("disabled plugin assembly stays unloaded", !AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "InkCanvas.Modules.AiAssistant"));
                return;
            }
            await Task.Delay(150);
            var window = shell.OpenWindows.Single(window => window.GetType().Name == "AssistantWindow");
            var frame = (WindowFrame)window.Content;
            Check("native assistant frame", window.Title == "AI 助手" && window.SystemBackdrop is DesktopAcrylicBackdrop);
            Check("empty chat makes no inference request", server.Posts == 0);
            Check("virtual list is the scroll owner", Descendants(frame).OfType<ListView>().Count() == 1);
            Check("removed solve controls and notices", !Descendants(frame).OfType<Button>().Any(button => new[] { "贴回白板", "讲解方式", "Agent 能力" }.Contains(AutomationProperties.GetName(button)))
                && !Descendants(frame).OfType<InfoBar>().Any());
            shell.ToggleBoard();
            var input = Descendants(frame).OfType<TextBox>().Single(text => AutomationProperties.GetName(text) == "消息内容");
            input.Text = "请增加一页并整理备课建议";
            Invoke(FindButton(frame, "发送 (Enter)"));
            await Wait(() => LastTurn(window)?.GetType().GetProperty("State")?.GetValue(LastTurn(window)) as string == "done");
            Check("built-in agent executes native tool and continues", services.Board.PageCount == 2 && server.ToolResults > 0 && Answer(window).Contains("备课建议", StringComparison.Ordinal));
            Check("model receives teacher prompt and history", server.TeacherPrompt);
            Check("copy is available without paste", Descendants(frame).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "复制回答"));
            string textPath = Path.Combine(directory, "lesson.txt"); await File.WriteAllTextAsync(textPath, "七年级课堂材料——分组讨论与形成性评价");
            var assembly = window.GetType().Assembly;
            var attachmentType = assembly.GetType("InkCanvas.Modules.AiAssistant.AssistantAttachment");
            var file = Activator.CreateInstance(attachmentType); attachmentType.GetProperty("Path").SetValue(file, textPath);
            window.GetType().GetMethod("AddAttachment").Invoke(window, new[] { file });
            var png = await Features.Board.BoardPreview.RenderAsync(services.Board);
            var task = (Task)assembly.GetType("InkCanvas.Modules.AiAssistant.AssistantAttachments").GetMethod("FromImageAsync").Invoke(null, new object[] { png, "课堂图片" });
            await task;
            var image = task.GetType().GetProperty("Result").GetValue(task);
            window.GetType().GetMethod("AddAttachment").Invoke(window, new[] { image });
            input.Text = "参考附件生成活动安排"; Invoke(FindButton(frame, "发送 (Enter)"));
            await Wait(() => State(window) == "done");
            Check("image compressed and text file read", server.ImageReceived && server.FileTextReceived
                && ((string)attachmentType.GetProperty("Image").GetValue(image)).StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal));
            Invoke(FindButton(frame, "新建对话")); await Task.Delay(70);
            Check("new chat has independent empty history", Descendants(frame).OfType<ListView>().Single().Items.Count == 0);
            input.Text = "请生成本周教学计划"; Invoke(FindButton(frame, "发送 (Enter)")); await Wait(() => State(window) == "done");
            var chats = Descendants(frame).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == "对话历史");
            Check("history includes multiple chats", chats.Items.Count == 2);
            chats.SelectedIndex = 1; await Task.Delay(60);
            Check("switching restores earlier chat", Descendants(frame).OfType<ListView>().Single().Items.Count == 2);
            foreach (int theme in new[] { 1, 2 })
            {
                WindowChrome.ApplyTheme(window, theme); await Task.Delay(70);
                window.AppWindow.Resize(new((int)(380 * WindowChrome.GetDpiScale(window)), (int)(760 * WindowChrome.GetDpiScale(window))));
                await Task.Delay(70);
                var background = frame.Background; frame.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.WhiteSmoke : Windows.UI.Color.FromArgb(255, 32, 32, 32));
                try { await BoardWindowRegression.SaveVisual(frame, Path.Combine(directory, "assistant-" + theme + ".png")); }
                finally { frame.Background = background; }
                Check("touch composer fits theme " + theme, new[] { "添加图片或文件", "添加当前画布", "发送 (Enter)", "思考设置" }.All(name =>
                {
                    var button = FindButton(frame, name); var bounds = button.TransformToVisual(frame).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
                    return button.ActualWidth >= 39 && bounds.Left >= 0 && bounds.Right <= frame.ActualWidth + 1;
                }));
            }
            string boardPath = Path.Combine(directory, "assistant.icink");
            await services.Board.SaveBoardAsync(boardPath);
            using (var json = JsonDocument.Parse(services.Board.ReadExtension("inkcanvas.ai-assistant")))
            {
                Check("one board contains every conversation", json.RootElement.GetProperty("Chats").GetArrayLength() == 2);
                var attachment = json.RootElement.GetProperty("Chats")[0].GetProperty("Turns")[1].GetProperty("Attachments")[0];
                Check("file attachment stores path only", attachment.GetProperty("Path").GetString() == textPath && attachment.GetProperty("Image").GetString() == "");
            }
            window.Close();
            await services.Board.LoadBoardAsync(boardPath); assistant.Open(); await Task.Delay(100);
            window = shell.OpenWindows.Single(window => window.GetType().Name == "AssistantWindow"); frame = (WindowFrame)window.Content;
            Check("reopening board restores history", Descendants(frame).OfType<ComboBox>().Single(combo => AutomationProperties.GetName(combo) == "对话历史").Items.Count == 2);
            input = Descendants(frame).OfType<TextBox>().Single(text => AutomationProperties.GetName(text) == "消息内容");
            server.RejectExtendedNext = true;
            input.Text = "测试附件回退"; Invoke(FindButton(frame, "发送 (Enter)")); await Wait(() => State(window) == "done");
            var fallbackTurn = LastTurn(window);
            Check("unsupported multimodal request falls back visibly", server.Fallbacks == 1
                && !string.IsNullOrEmpty((string)fallbackTurn.GetType().GetProperty("Fallback").GetValue(fallbackTurn)));
            server.DelayNext = true; int posts = server.Posts;
            input.Text = "生成长一点的课堂讨论"; Invoke(FindButton(frame, "发送 (Enter)")); await Wait(() => server.Posts > posts);
            Invoke(FindButton(frame, "停止生成")); await Wait(() => State(window) == "stopped");
            Check("stop cancels the active turn", State(window) == "stopped");
            var behavior = (StackPanel)assistant.GetType().GetMethod("CreateBehaviorSettingsView").Invoke(assistant, null);
            behavior.Children.OfType<ToggleSwitch>().Single().IsOn = false; await Task.Delay(30);
            input.Text = "继续整理教学计划"; Invoke(FindButton(frame, "发送 (Enter)")); await Wait(() => State(window) == "done");
            Check("plugin setting disables tool declarations", !server.LastHadTools);
            var thinkingButton = FindButton(frame, "思考设置"); thinkingButton.Flyout.ShowAt(thinkingButton); await Task.Delay(60);
            var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot).Single(popup => Descendants(popup.Child).OfType<ToggleSwitch>().Any());
            var effort = Descendants(popup.Child).OfType<ComboBox>().Single();
            Check("thinking supports three strengths with high default", effort.Items.Count == 3 && effort.SelectedIndex == 1
                && Descendants(popup.Child).OfType<ToggleSwitch>().Single().IsOn);
            thinkingButton.Flyout.Hide();
            // Populate through the public workspace model to exercise recycling with a long history.
            var workspace = window.GetType().GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
            var chat = workspace.GetType().GetProperty("Active").GetValue(workspace);
            var turns = (System.Collections.IList)chat.GetType().GetProperty("Turns").GetValue(chat);
            var turnType = assembly.GetType("InkCanvas.Modules.AiAssistant.AssistantChatTurn");
            for (int index = 0; index < 300; index++)
            {
                var turn = Activator.CreateInstance(turnType); turnType.GetProperty("Question").SetValue(turn, "课堂讨论 " + index);
                turnType.GetProperty("Answer").SetValue(turn, string.Concat(Enumerable.Repeat("围绕目标设计活动，并记录课堂反馈。", 36))); turnType.GetProperty("State").SetValue(turn, "done"); turns.Add(turn);
            }
            window.GetType().GetMethod("SetWorkspace").Invoke(window, new[] { workspace }); await Task.Delay(250);
            var list = Descendants(frame).OfType<ListView>().Single();
            Check("long history realizes a bounded number of cards", list.Items.Count > 300 && Descendants(list).Count(item => item.GetType().Name == "AssistantChatCard") < 50);
            list.ScrollIntoView(list.Items[0]); await Task.Delay(100); list.ScrollIntoView(list.Items[^1]); await Task.Delay(100);
            Check("recycled cards display the latest record", Descendants(list).OfType<TextBlock>().Any(text => text.Text == "课堂讨论 299"));
            behavior.Children.OfType<ToggleSwitch>().Single().IsOn = true;
            int before = turns.Count;
            input.Text = "依据历史安排下一节课"; Invoke(FindButton(frame, "发送 (Enter)")); await Wait(() => State(window) == "done");
            Check("automatic compaction preserves full visible history", (int)chat.GetType().GetProperty("SummarizedTurns").GetValue(chat) > 0
                && turns.Count == before + 1 && server.Compactions > 0);
            using (var toolRegistration = services.AssistantTools.Register(new("diagnostic_context_data", "读取诊断上下文资料", AssistantPlugin.EmptyParameters)
                { ReadOnly = true }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new AssistantToolResult(new string('文', 14000))); }))
            {
                server.ToolLoopRemaining = 28;
                input.Text = "读取多批资料并继续完成教学计划"; Invoke(FindButton(frame, "发送 (Enter)")); await Wait(() => State(window) == "done");
                var toolTurn = LastTurn(window);
                var toolRecords = (System.Collections.IList)toolTurn.GetType().GetProperty("Tools").GetValue(toolTurn);
                Check("running agent compacts completed tool exchanges and continues", toolRecords.Count == 28
                    && (int)toolTurn.GetType().GetProperty("SummarizedTools").GetValue(toolTurn) > 0
                    && !string.IsNullOrEmpty((string)toolTurn.GetType().GetProperty("ToolSummary").GetValue(toolTurn)));
                Check("tool compaction retains original results", toolRecords.Cast<object>().All(record => ((string)record.GetType().GetProperty("Result").GetValue(record)).Length == 14000));
            }
            window.Close();
            services.Modules.Invoke<IAiAssistantService>(_ => throw new InvalidOperationException("diagnostic stop"));
            var withoutPlugin = new Features.Board.BoardService();
            try
            {
                await withoutPlugin.LoadBoardAsync(boardPath); await withoutPlugin.SaveBoardAsync(Path.Combine(directory, "without-plugin.icink"));
                Check("board round-trips with AI plugin stopped", withoutPlugin.PageCount == 2 && withoutPlugin.ReadExtension("inkcanvas.ai-assistant") is { Length: > 0 });
            }
            finally { foreach (var page in withoutPlugin.Pages) page.Dispose(); }
        }
        catch (Exception exception)
        {
            var failedWindow = shell.OpenWindows.FirstOrDefault(window => window.GetType().Name == "AssistantWindow");
            var turn = failedWindow is null ? null : LastTurn(failedWindow);
            checks.Add(new { check = "AI assistant integration", passed = false, detail = exception.ToString(), posts = server.Posts,
                state = turn?.GetType().GetProperty("State").GetValue(turn), error = turn?.GetType().GetProperty("Error").GetValue(turn) });
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "ai-plugin.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static object LastTurn(Window window)
    {
        var workspace = window.GetType().GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        var chat = workspace.GetType().GetProperty("Active").GetValue(workspace);
        var turns = (System.Collections.IList)chat.GetType().GetProperty("Turns").GetValue(chat);
        return turns.Count == 0 ? null : turns[turns.Count - 1];
    }
    private static string State(Window window) => (string)LastTurn(window)?.GetType().GetProperty("State").GetValue(LastTurn(window));
    private static string Answer(Window window) => (string)LastTurn(window)?.GetType().GetProperty("Answer").GetValue(LastTurn(window)) ?? "";
    private static async Task Wait(Func<bool> condition)
    {
        for (int index = 0; index < 500; index++) { if (condition()) return; await Task.Delay(20); }
        throw new TimeoutException("Assistant integration timed out.");
    }
    private static Button FindButton(DependencyObject root, string name) => Descendants(root).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed class FixtureServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Address { get; }
        public int Posts, ToolResults, Fallbacks, Compactions, ToolLoopRemaining;
        public bool TeacherPrompt, ImageReceived, FileTextReceived, RejectExtendedNext, DelayNext, LastHadTools;
        public FixtureServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Address = "http://127.0.0.1:" + port + "/v1"; _listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); _listener.Start(); _ = Run();
        }
        private async Task Run()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream); string request = await reader.ReadToEndAsync(); Posts++;
                    using var json = JsonDocument.Parse(request);
                    LastHadTools = json.RootElement.TryGetProperty("tools", out _);
                    if (RejectExtendedNext)
                    {
                        RejectExtendedNext = false; Fallbacks++;
                        context.Response.StatusCode = 400; context.Response.Close(); continue;
                    }
                    bool delayed = DelayNext; DelayNext = false;
                    if (delayed) await Task.Delay(250);
                    if (json.RootElement.TryGetProperty("stream", out var streaming) && streaming.ValueKind == JsonValueKind.False)
                    {
                        Compactions++;
                        byte[] summary = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"教师已确认课程目标，需安排活动、记录课堂反馈并继续下一节课。\"}}]}");
                        context.Response.ContentType = "application/json"; context.Response.ContentLength64 = summary.Length;
                        await context.Response.OutputStream.WriteAsync(summary); context.Response.Close(); continue;
                    }
                    var messages = json.RootElement.GetProperty("messages");
                    TeacherPrompt |= messages[0].GetProperty("content").GetString().Contains("协助教师", StringComparison.Ordinal);
                    ImageReceived |= request.Contains("image_url", StringComparison.Ordinal);
                    FileTextReceived |= request.Contains("\\u4E03\\u5E74", StringComparison.OrdinalIgnoreCase) || request.Contains("七年级", StringComparison.Ordinal);
                    bool result = messages.EnumerateArray().Any(message => message.GetProperty("role").GetString() == "tool");
                    if (result) ToolResults++;
                    bool tool = Posts == 1;
                    string packet = tool ? """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"board_page","arguments":"{\"action\":\"add\"}"}}]}}]}"""
                        : """{"choices":[{"delta":{"content":"## 备课建议\n结合课程目标安排小组讨论，并记录课堂反馈。"}}]}""";
                    if (ToolLoopRemaining > 0)
                    {
                        ToolLoopRemaining--;
                        packet = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { new
                        { index = 0, id = "context_" + ToolLoopRemaining, type = "function", function = new { name = "diagnostic_context_data", arguments = "{}" } } } } } } });
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes("data: " + packet + "\n\ndata: [DONE]\n\n");
                    context.Response.ContentType = "text/event-stream"; context.Response.ContentLength64 = bytes.Length;
                    try { await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); }
                    catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException) { }
                }
            }
            catch (Exception) when (!_listener.IsListening) { }
        }
        public void Dispose() => _listener.Close();
    }
}
