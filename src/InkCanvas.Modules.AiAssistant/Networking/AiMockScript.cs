// @parity harmony/platform/src/main/ets/assistant/AiClient.ets
using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Platform.Ai;

/// <summary>
/// 本地 mock 剧本：端点为空时由 <see cref="AiClient"/> 逐条回放，事件顺序与真实 SSE 流一致，
/// 覆盖公式转写、自动求解、图形释义与自由问答四条路径，供流式渲染 / 中断 / 错误态自测。
/// </summary>
public static class AiMockScript
{
    /// <summary>板书书写 mock：formula / text / wordlist / poem 各一块，附一条备注。</summary>
    internal const string BoardWritePlanJson =
        """
        {
          "type": "board_plan",
          "schema": 1,
          "intent": "write",
          "target": { "board": "main", "pageIndex": 0 },
          "originals": [
            "1/2 x² - 3x + 2 = 0",
            "求根：x = 2 或 x = 3",
            "判别式 Δ = 1"
          ],
          "confidence": 0.78,
          "warnings": [
            "整体置信度 0.78 偏低，请复核公式系数",
            "原文疑似笔误：第二行 x1 可能应为 x_1"
          ],
          "blocks": [
            {
              "id": "b1",
              "kind": "formula",
              "latex": "\\frac{1}{2}x^2 - 3x + 2 = 0",
              "rect": { "x": 0.06, "y": 0.10, "w": 0.42, "h": 0.14 },
              "style": { "fontSize": 28, "align": "center", "columns": 1, "lineHeight": 1.4 },
              "confidence": 0.72,
              "note": "一次项系数疑似笔误，已按 -3x 整理"
            },
            {
              "id": "b2",
              "kind": "text",
              "text": "一元二次方程综合讲解",
              "confidence": 0.95,
              "rect": { "x": 0.06, "y": 0.28, "w": 0.42, "h": 0.10 },
              "style": { "fontSize": 24, "align": "left", "columns": 1, "lineHeight": 1.4 }
            },
            {
              "id": "b3",
              "kind": "wordlist",
              "lines": ["判别式", "配方法", "因式分解"],
              "confidence": 0.88,
              "rect": { "x": 0.54, "y": 0.10, "w": 0.24, "h": 0.28 },
              "style": { "fontSize": 24, "align": "left", "columns": 1, "lineHeight": 1.4 }
            },
            {
              "id": "b4",
              "kind": "poem",
              "title": "静夜思",
              "author": "唐·李白",
              "lines": ["床前明月光", "疑是地上霜", "举头望明月", "低头思故乡"],
              "confidence": 0.9,
              "note": "标题根据正文自动生成",
              "rect": { "x": 0.54, "y": 0.42, "w": 0.42, "h": 0.30 },
              "style": { "fontSize": 26, "align": "center", "columns": 1, "lineHeight": 1.6 }
            }
          ],
          "notes": ["第 2 块标题为自动生成，可按需修改"]
        }
        """;

    /// <summary>SSE 以 LF 重组 data 行，mock 统一为 LF，避免 Windows 源码换行造成回放差异。</summary>
    private static readonly string BoardWritePlan = BoardWritePlanJson.Replace("\r\n", "\n");

    /// <summary>
    /// 按模式返回固定的 mock 事件序列，最后一条恒为 <see cref="AssistantEventKind.Done"/>。
    /// 讲题模式为 <see cref="AssistantTeachingMode.Hint"/> 时只回放文字提示，
    /// 不产生公式、分步与图形标注，模拟服务端按模式裁剪回答。
    /// </summary>
    public static IReadOnlyList<AssistantEvent> Build(
        AssistantMode mode,
        AssistantTeachingMode teaching = AssistantTeachingMode.StepByStep,
        string purpose = "",
        string difficulty = "")
    {
        var events = new List<AssistantEvent>();

        if (string.Equals(purpose, "board_write", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new AssistantEvent
            {
                Kind = AssistantEventKind.BoardPlan,
                Text = BoardWritePlan,
            });
            events.Add(new AssistantEvent { Kind = AssistantEventKind.Done });
            return events;
        }

        if (string.Equals(purpose, "region", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(Token("已知条件与所求已重新识别。\n"));
            events.Add(new AssistantEvent { Kind = AssistantEventKind.Done });
            return events;
        }

        if (string.Equals(purpose, "variant", StringComparison.OrdinalIgnoreCase))
        {
            bool advanced = string.Equals(difficulty, "advanced", StringComparison.OrdinalIgnoreCase);
            events.Add(Token(advanced ? "【提高变式】\n" : "【基础变式】\n"));
            events.Add(Token(advanced
                ? "已知函数 f(x)=x^3-3x，讨论其单调性与极值。\n"
                : "已知函数 f(x)=x^2-2x+1，求 f(3) 的值。\n"));
            events.Add(Token(advanced ? "答案：极小值 f(1)=-2，极大值 f(-1)=2。\n" : "答案：4\n"));
            events.Add(Token("知识点：函数求值与单调性。\n"));
            events.Add(Step(1, "先整理已知条件与所求", ""));
            events.Add(new AssistantEvent { Kind = AssistantEventKind.Done });
            return events;
        }

        switch (mode)
        {
            case AssistantMode.Formula:
                events.Add(Token("已识别到手写公式。\n\n"));
                events.Add(Formula("\\frac{1}{2}x^2 - 3x + 2 = 0"));
                events.Add(Token("\n\n对照原迹：二次项系数为 $\\frac{1}{2}$，一次项为 $-3x$，常数项为 $2$。\n"));
                events.Add(Step(1, "两边乘以 2，消去分式系数", "x^2 - 6x + 4 = 0"));
                events.Add(Step(2, "移项后配方", "(x-3)^2 = 5"));
                events.Add(Token("\n可继续追问该式的判别式或图像。"));
                break;

            case AssistantMode.Solve:
                events.Add(Token("下面开始求解。\n\n"));
                events.Add(Step(1, "原方程为", "x^2 - 5x + 6 = 0"));
                events.Add(Step(2, "因式分解", "(x-2)(x-3) = 0"));
                events.Add(Step(3, "因此", "x_1 = 2,\\ x_2 = 3"));
                events.Add(Formula("x_1 = 2,\\ x_2 = 3"));
                events.Add(Token("\n\n检验：把 $x=2$ 与 $x=3$ 代回原式均成立。"));
                break;

            case AssistantMode.Shape:
                events.Add(Token("识别到直角三角形。\n\n"));
                events.Add(Shape("直角三角形", new double[] { 120, 80 }, new double[] { 280, 80 }, new double[] { 120, 240 }, new double[] { 120, 80 }));
                events.Add(Token("已知两条直角边分别为 3 与 4，斜边为 5。\n\n"));
                events.Add(Token("可推导性质：两锐角互余，且满足 $a^2+b^2=c^2$。"));
                break;

            default:
                events.Add(Token("这是框选内容的要点：\n\n"));
                events.Add(Token("1. 先识别手写公式的结构与符号；\n"));
                events.Add(Token("2. 再给出可验证的推导步骤；\n"));
                events.Add(Token("3. 最后给出一句话结论。\n\n"));
                events.Add(Step(1, "先确认题目条件与所求", ""));
                events.Add(Step(2, "按条件逐步推导，并回代检验", ""));
                events.Add(Shape("题目图形",
                    new double[] { 0.18, 0.22 },
                    new double[] { 0.46, 0.22 },
                    new double[] { 0.46, 0.56 },
                    new double[] { 0.18, 0.22 }));
                events.Add(Formula("E = mc^2"));
                events.Add(Token("如需更精确的识别，可重新框选更清晰的区域。"));
                break;
        }

        events.Add(new AssistantEvent { Kind = AssistantEventKind.Done });
        if (teaching != AssistantTeachingMode.Hint) return events;

        return events
            .Where(ev => ev.Kind is AssistantEventKind.Token or AssistantEventKind.Done)
            .ToList();
    }

    private static AssistantEvent Token(string text) => new()
    {
        Kind = AssistantEventKind.Token,
        Text = text,
    };

    private static AssistantEvent Formula(string latex) => new()
    {
        Kind = AssistantEventKind.Formula,
        Latex = latex,
    };

    private static AssistantEvent Step(int index, string text, string latex) => new()
    {
        Kind = AssistantEventKind.Step,
        Index = index,
        Text = text,
        Latex = latex,
    };

    private static AssistantEvent Shape(string title, params double[][] points) => new()
    {
        Kind = AssistantEventKind.ShapeAnnotation,
        Title = title,
        Points = points,
    };
}