using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InkCanvas.AnalyticGeometry.Handwriting;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Ink.Rendering;

namespace InkCanvas.Core.Tests.Evaluation;

/// <summary>
/// 教学评测驱动（P0 交付物 4）：读取 <c>tests/eval/fixtures</c> 的确定性 fixtures，
/// 调用 <see cref="MathInkRecognizer"/>、<see cref="AssistantBoardPlan"/>、<see cref="EquationVerifier"/>、
/// <see cref="FormulaLayout"/> 与 <see cref="AiCardEditState"/>，聚合为 <c>metrics.json</c>。
/// <para>
/// 默认 <c>mock</c>：只用离线 fixtures，不请求任何模型。真实评测需手动执行并接入真实端点；
/// 本驱动绝不伪造真实模型准确率，real 状态固定标记为未运行/不可用。
/// </para>
/// </summary>
public static class TeachingEvalRunner
{
    public const string MetricsSchema = "inkcanvas-teaching-eval/1";

    private static readonly JsonSerializerOptions FixtureOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixtureDirectory => Path.Combine(RepoRoot, "tests", "eval", "fixtures");

    public static string MetricsPath => Path.Combine(RepoRoot, "artifacts", "eval", "metrics.json");

    public static TeachingEvalResult Run() => Run(null);

    /// <param name="realRequested">
    /// null = 读取 <c>INKCANVAS_EVAL_MODE=real</c>；显式 true 用于测试 real 状态分支。
    /// 无论取值如何都不会伪造真实模型准确率。
    /// </param>
    public static TeachingEvalResult Run(bool? realRequested)
    {
        bool real = realRequested ?? string.Equals(
            Environment.GetEnvironmentVariable("INKCANVAS_EVAL_MODE"), "real", StringComparison.OrdinalIgnoreCase);

        var details = new List<EvalCaseResult>();
        var fixtures = new List<string>();

        string handwritingPath = Path.Combine(FixtureDirectory, "handwriting_strokes.json");
        HandwritingEvalMetric handwriting = EvaluateHandwriting(handwritingPath, details);
        fixtures.Add(Relative(handwritingPath));

        FormulaEvalMetric formula = EvaluateFormulas(
            Path.Combine(FixtureDirectory, "formula_latex.json"), details);
        fixtures.Add("tests/eval/fixtures/formula_latex.json");

        string[] boardPlanFiles =
        {
            "board_plan_words.json",
            "board_plan_poem.json",
            "board_plan_low_confidence.json",
            "board_plan_originals.json",
        };

        var plans = new List<LoadedBoardPlan>();
        foreach (string file in boardPlanFiles)
        {
            string path = Path.Combine(FixtureDirectory, file);
            plans.Add(LoadBoardPlan(path));
            fixtures.Add(Relative(path));
        }

        OriginalEvalMetric original = EvaluateOriginals(plans, details);
        AdoptionEvalMetric adoption = EvaluateAdoption(plans, details);

        string editPath = Path.Combine(FixtureDirectory, "edit_before_after.json");
        EditEvalMetric edit = EvaluateEdits(editPath, details);
        fixtures.Add(Relative(editPath));

        var metrics = new TeachingEvalMetrics(handwriting, formula, original, edit, adoption);
        var realStatus = real
            ? new TeachingEvalRealStatus(
                "unavailable",
                "真实模型评测需要手动提供端点与真实课堂样本；本驱动未调用任何远端模型，未生成真实准确率。")
            : new TeachingEvalRealStatus(
                "not-run",
                "CI 默认仅运行离线 fixtures；设置 INKCANVAS_EVAL_MODE=real 也不会伪造真实模型准确率。");

        var result = new TeachingEvalResult(
            MetricsSchema,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "mock",
            metrics,
            fixtures,
            realStatus,
            new[]
            {
                "所有指标均来自 tests/eval/fixtures 的确定性样例，source=fixture:synthetic。",
                "mock 模式不访问网络、不调用 AI 端点，不能代表真实模型/真实课堂准确率。",
                "handwriting.exact 为表达式完全一致率；charAccuracy 为 1-归一化编辑距离均值。",
                "formula.structAccuracy 为 LaTeX 结构解析结果与期望一致率（含应失败的畸形样例）；verificationAccuracy 为方程代入自检一致率。",
                "original.lineAccuracy 为 board_plan 原文行与期望逐行一致率。",
                "edit.changedRatio 为教师编辑前后文本的归一化编辑距离均值。",
                "adoption.selectedRatio 为教师勾选采用的块占比均值。",
            },
            details);

        WriteMetrics(result);
        return result;
    }

    // ---- suites -------------------------------------------------------------

    private static HandwritingEvalMetric EvaluateHandwriting(string path, List<EvalCaseResult> details)
    {
        HandwritingFixture fixture = Load<HandwritingFixture>(path);
        int cases = 0, exactMatches = 0;
        double charAccuracySum = 0;

        foreach (HandwritingCase item in fixture.Cases ?? new List<HandwritingCase>())
        {
            cases++;
            var strokes = (item.Strokes ?? new List<StrokeDto>())
                .Select(stroke => new MathInkStroke(
                    stroke.Id,
                    (stroke.Points ?? new List<List<double>>())
                        .Where(point => point is { Count: >= 2 })
                        .Select(point => new InkSample(point[0], point[1]))
                        .ToArray()))
                .ToArray();

            Dictionary<int, string> overrides = null;
            if (item.SymbolOverrides is { Count: > 0 })
            {
                overrides = new Dictionary<int, string>();
                foreach (KeyValuePair<string, string> pair in item.SymbolOverrides)
                    if (int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        overrides[id] = pair.Value;
            }

            string actual = MathInkRecognizer.Recognize(strokes, overrides).Expression ?? "";
            string expected = item.Expected ?? "";
            bool passed = string.Equals(actual, expected, StringComparison.Ordinal);
            if (passed) exactMatches++;
            charAccuracySum += CharAccuracy(actual, expected);

            details.Add(new EvalCaseResult(
                "handwriting", item.Id ?? "", expected, actual, passed, null));
        }

        return new HandwritingEvalMetric(
            cases,
            exactMatches,
            Rate(exactMatches, cases),
            Rate(charAccuracySum, cases),
            "fixture:synthetic");
    }

    private static FormulaEvalMetric EvaluateFormulas(string path, List<EvalCaseResult> details)
    {
        FormulaFixture fixture = Load<FormulaFixture>(path);
        int cases = 0, structMatches = 0, verificationMatches = 0;

        foreach (FormulaCase item in fixture.Cases ?? new List<FormulaCase>())
        {
            cases++;
            FormulaLayout layout = FormulaLayout.Create(item.Latex ?? "", 30, out string error);
            bool structOk = layout is not null;
            bool structPassed = structOk == item.ExpectedStruct;
            if (structPassed) structMatches++;

            VerificationStatus status = EquationVerifier.Verify(item.Latex ?? "").Status;
            string actualVerification = status.ToString();
            bool verificationPassed = string.Equals(
                actualVerification, item.ExpectedVerification ?? "", StringComparison.Ordinal);
            if (verificationPassed) verificationMatches++;

            string structActual = structOk ? "parsed" : "failed:" + (error ?? "");
            details.Add(new EvalCaseResult(
                "formula", item.Id ?? "",
                $"struct={(item.ExpectedStruct ? "parsed" : "failed")};verify={item.ExpectedVerification}",
                $"struct={structActual};verify={actualVerification}",
                structPassed && verificationPassed,
                null));
        }

        return new FormulaEvalMetric(
            cases,
            Rate(structMatches, cases),
            Rate(verificationMatches, cases),
            "fixture:synthetic");
    }

    private static OriginalEvalMetric EvaluateOriginals(
        IReadOnlyList<LoadedBoardPlan> plans, List<EvalCaseResult> details)
    {
        int lines = 0, matches = 0;
        foreach (LoadedBoardPlan loaded in plans)
        {
            List<string> expected = loaded.Expected?.OriginalLines ?? new List<string>();
            IReadOnlyList<string> actual = loaded.Plan.Originals ?? Array.Empty<string>();
            for (int i = 0; i < expected.Count; i++)
            {
                lines++;
                bool passed = i < actual.Count && string.Equals(actual[i], expected[i], StringComparison.Ordinal);
                if (passed) matches++;
                details.Add(new EvalCaseResult(
                    "original", loaded.FileName + "#" + i, expected[i],
                    i < actual.Count ? actual[i] : "<missing>", passed, null));
            }
        }

        return new OriginalEvalMetric(lines, Rate(matches, lines), "fixture:synthetic");
    }

    private static AdoptionEvalMetric EvaluateAdoption(
        IReadOnlyList<LoadedBoardPlan> plans, List<EvalCaseResult> details)
    {
        int count = 0;
        double ratioSum = 0;
        foreach (LoadedBoardPlan loaded in plans)
        {
            count++;
            List<string> selectedIds = loaded.Expected?.SelectedIds ?? new List<string>();
            AssistantBoardPlan selected = loaded.Plan.SelectBlocks(selectedIds);
            int total = loaded.Plan.Blocks?.Count ?? 0;
            double ratio = total == 0 ? 0 : selected.Blocks.Count / (double)total;
            ratioSum += ratio;

            double expectedRatio = loaded.Expected?.SelectedRatio ?? 0;
            bool passed = Math.Abs(ratio - expectedRatio) < 1e-6;
            details.Add(new EvalCaseResult(
                "adoption", loaded.FileName,
                expectedRatio.ToString("0.###", CultureInfo.InvariantCulture),
                ratio.ToString("0.###", CultureInfo.InvariantCulture), passed, null));
        }

        return new AdoptionEvalMetric(count, Rate(ratioSum, count), "fixture:synthetic");
    }

    private static EditEvalMetric EvaluateEdits(string path, List<EvalCaseResult> details)
    {
        EditFixture fixture = Load<EditFixture>(path);
        int cases = 0;
        double ratioSum = 0;

        foreach (EditCase item in fixture.Cases ?? new List<EditCase>())
        {
            cases++;
            string kind = string.IsNullOrWhiteSpace(item.Kind) ? "text" : item.Kind.Trim().ToLowerInvariant();
            string before = item.Before ?? "";
            string after = item.After ?? "";

            var metadata = new AiPasteMetadata
            {
                Kind = AiPasteKind.BoardPlan,
                Schema = 1,
                BlockKind = kind,
                Text = kind == "formula" ? "" : before,
                Latex = kind == "formula" ? before : "",
                Lines = kind == "formula"
                    ? new List<string>()
                    : AiCardEditState.SplitLines(before),
            };

            AiCardEditState state = AiCardEditState.FromMetadata(metadata);
            AiPasteMetadata updated = state.ApplyTo(metadata, "", "", after);
            string afterBody = AiCardEditState.FromMetadata(updated).Body;
            double ratio = ChangedRatio(state.Body, afterBody);
            ratioSum += ratio;

            double expectedRatio = item.ExpectedChangedRatio;
            bool passed = Math.Abs(ratio - expectedRatio) < 1e-6;
            details.Add(new EvalCaseResult(
                "edit", item.Id ?? "",
                expectedRatio.ToString("0.####", CultureInfo.InvariantCulture),
                ratio.ToString("0.####", CultureInfo.InvariantCulture), passed, null));
        }

        return new EditEvalMetric(cases, Rate(ratioSum, cases), "fixture:synthetic");
    }

    // ---- board plan fixtures ------------------------------------------------

    private static LoadedBoardPlan LoadBoardPlan(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        if (!AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error))
            throw new InvalidOperationException($"{Path.GetFileName(path)} 解析失败：{error}");

        BoardPlanExpected expected = null;
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            if (document.RootElement.TryGetProperty("expected", out JsonElement element))
                expected = JsonSerializer.Deserialize<BoardPlanExpected>(element.GetRawText(), FixtureOptions);
        }

        return new LoadedBoardPlan(Path.GetFileName(path), plan, expected);
    }

    // ---- helpers ------------------------------------------------------------

    private static T Load<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), FixtureOptions)
           ?? throw new InvalidOperationException($"fixture 解析失败：{path}");

    private static void WriteMetrics(TeachingEvalResult result)
    {
        string directory = Path.GetDirectoryName(MetricsPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(MetricsPath, JsonSerializer.Serialize(result, OutputOptions), Encoding.UTF8);
    }

    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static double Rate(double value, int count)
        => count <= 0 ? 0 : value / count;

    /// <summary>1 - 归一化 Levenshtein 距离；完全一致为 1，长度全不同为 0。</summary>
    private static double CharAccuracy(string actual, string expected)
        => 1.0 - NormalizedDistance(actual, expected);

    private static double ChangedRatio(string before, string after)
        => NormalizedDistance(before, after);

    private static double NormalizedDistance(string left, string right)
    {
        left ??= "";
        right ??= "";
        if (left.Length == 0 && right.Length == 0) return 0;
        int max = Math.Max(left.Length, right.Length);
        if (max == 0) return 0;
        return Levenshtein(left, right) / (double)max;
    }

    private static int Levenshtein(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++) previous[j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "eval", "fixtures")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 tests/eval/fixtures，无法定位仓库根目录。");
    }

    // ---- DTOs ---------------------------------------------------------------

    private sealed record LoadedBoardPlan(string FileName, AssistantBoardPlan Plan, BoardPlanExpected Expected);

    public sealed record TeachingEvalResult(
        string Schema,
        string GeneratedAt,
        string Mode,
        TeachingEvalMetrics Metrics,
        IReadOnlyList<string> Fixtures,
        TeachingEvalRealStatus Real,
        IReadOnlyList<string> Notes,
        IReadOnlyList<EvalCaseResult> Details);

    public sealed record TeachingEvalMetrics(
        HandwritingEvalMetric Handwriting,
        FormulaEvalMetric Formula,
        OriginalEvalMetric Original,
        EditEvalMetric Edit,
        AdoptionEvalMetric Adoption);

    public sealed record HandwritingEvalMetric(
        int Cases, int ExactMatches, double Exact, double CharAccuracy, string Source);

    public sealed record FormulaEvalMetric(
        int Cases, double StructAccuracy, double VerificationAccuracy, string Source);

    public sealed record OriginalEvalMetric(int Lines, double LineAccuracy, string Source);

    public sealed record EditEvalMetric(int Cases, double ChangedRatio, string Source);

    public sealed record AdoptionEvalMetric(int Plans, double SelectedRatio, string Source);

    public sealed record TeachingEvalRealStatus(string Status, string Reason);

    public sealed record EvalCaseResult(
        string Suite, string Id, string Expected, string Actual, bool Passed, string Note);

    private sealed class HandwritingFixture
    {
        public string Source { get; set; } = "";
        public List<HandwritingCase> Cases { get; set; } = new();
    }

    private sealed class HandwritingCase
    {
        public string Id { get; set; } = "";
        public List<StrokeDto> Strokes { get; set; } = new();
        public Dictionary<string, string> SymbolOverrides { get; set; }
        public string Expected { get; set; } = "";
    }

    private sealed class StrokeDto
    {
        public int Id { get; set; }
        public List<List<double>> Points { get; set; } = new();
    }

    private sealed class FormulaFixture
    {
        public string Source { get; set; } = "";
        public List<FormulaCase> Cases { get; set; } = new();
    }

    private sealed class FormulaCase
    {
        public string Id { get; set; } = "";
        public string Latex { get; set; } = "";
        public bool ExpectedStruct { get; set; }
        public string ExpectedVerification { get; set; } = "";
    }

    private sealed class EditFixture
    {
        public string Source { get; set; } = "";
        public List<EditCase> Cases { get; set; } = new();
    }

    private sealed class EditCase
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Before { get; set; } = "";
        public string After { get; set; } = "";
        public double ExpectedChangedRatio { get; set; }
    }

    private sealed class BoardPlanExpected
    {
        public List<string> BlockKinds { get; set; }
        public List<string> OriginalLines { get; set; }
        public int? OriginalBlocks { get; set; }
        public double Confidence { get; set; }
        public bool NeedsReview { get; set; }
        public int? Warnings { get; set; }
        public List<string> SelectedIds { get; set; }
        public double SelectedRatio { get; set; }
        public string VerifiedFormula { get; set; }
    }
}