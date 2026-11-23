using System.Globalization;
using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Tests.Evaluation;
using Xunit;
using TeachingEvalMetrics = InkCanvas.Core.Tests.Evaluation.TeachingEvalRunner.TeachingEvalMetrics;
using TeachingEvalResult = InkCanvas.Core.Tests.Evaluation.TeachingEvalRunner.TeachingEvalResult;

namespace InkCanvas.Core.Tests;

/// <summary>
/// P0 交付物 4：教学评测集 fixtures + metrics.json 驱动。
/// 断言指标由 fixtures 确定性计算，来源标注为 fixture:synthetic，且 real 状态不伪造准确率。
/// </summary>
public sealed class TeachingEvalMetricsTests
{
    [Fact]
    public void Run_ComputesFixtureMetricsAndWritesMetricsJson()
    {
        TeachingEvalResult result = TeachingEvalRunner.Run();

        Assert.Equal(TeachingEvalRunner.MetricsSchema, result.Schema);
        Assert.Equal("mock", result.Mode);

        TeachingEvalMetrics metrics = result.Metrics;
        Assert.Equal(5, metrics.Handwriting.Cases);
        Assert.Equal(5, metrics.Handwriting.ExactMatches);
        Assert.Equal(1.0, metrics.Handwriting.Exact, 6);
        Assert.Equal(1.0, metrics.Handwriting.CharAccuracy, 6);

        Assert.Equal(4, metrics.Formula.Cases);
        Assert.Equal(1.0, metrics.Formula.StructAccuracy, 6);
        Assert.Equal(1.0, metrics.Formula.VerificationAccuracy, 6);


        Assert.Equal(10, metrics.Original.Lines);
        Assert.Equal(1.0, metrics.Original.LineAccuracy, 6);

        Assert.Equal(4, metrics.Edit.Cases);
        Assert.Equal(0.45625, metrics.Edit.ChangedRatio, 5);

        Assert.Equal(4, metrics.Adoption.Plans);
        Assert.Equal(0.75, metrics.Adoption.SelectedRatio, 6);

        Assert.All(new[]
        {
            metrics.Handwriting.Source, metrics.Formula.Source, metrics.Original.Source,
            metrics.Edit.Source, metrics.Adoption.Source,
        }, source => Assert.Equal("fixture:synthetic", source));

        Assert.Equal("not-run", result.Real.Status);
        Assert.Contains("不会伪造真实模型准确率", result.Real.Reason);
        Assert.All(result.Details, detail => Assert.True(detail.Passed,
            $"{detail.Suite}/{detail.Id} 未通过：expected={detail.Expected} actual={detail.Actual}"));
        Assert.NotEmpty(result.Fixtures);

        Assert.True(File.Exists(TeachingEvalRunner.MetricsPath),
            "metrics.json 未生成：" + TeachingEvalRunner.MetricsPath);
        string json = File.ReadAllText(TeachingEvalRunner.MetricsPath);
        Assert.Contains("\"schema\": \"inkcanvas-teaching-eval/1\"", json);
        Assert.Contains("\"mode\": \"mock\"", json);
        Assert.Contains("fixture:synthetic", json);
        Assert.Contains("\"real\"", json);
    }

    [Fact]
    public void RealMode_ReportsUnavailableInsteadOfFabricatingAccuracy()
    {
        TeachingEvalResult result = TeachingEvalRunner.Run(realRequested: true);

        Assert.Equal("mock", result.Mode);
        Assert.Equal("unavailable", result.Real.Status);
        Assert.Contains("未生成真实准确率", result.Real.Reason);
        Assert.Equal("fixture:synthetic", result.Metrics.Handwriting.Source);
    }

    [Fact]
    public void BoardPlanFixtures_ParseToExpectedStructureAndOriginals()
    {
        AssertBoardPlan("board_plan_words.json", expectedKinds: new[] { "wordlist", "wordlist" },
            expectedConfidence: 0.94, expectedReview: false, expectedOriginals: 2);
        AssertBoardPlan("board_plan_poem.json", expectedKinds: new[] { "poem" },
            expectedConfidence: 0.9, expectedReview: false, expectedOriginals: 2);
        AssertBoardPlan("board_plan_originals.json", expectedKinds: new[] { "text", "poem" },
            expectedConfidence: 0.88, expectedReview: false, expectedOriginals: 3);

        string lowPath = Path.Combine(TeachingEvalRunner.FixtureDirectory, "board_plan_low_confidence.json");
        string lowJson = File.ReadAllText(lowPath);
        Assert.True(AssistantBoardPlan.TryParse(lowJson, out AssistantBoardPlan low, out string error), error);
        Assert.Equal(new[] { "formula", "text" }, low.Blocks.Select(block => block.Kind));
        Assert.True(low.NeedsReview);
        Assert.Equal(2, low.Warnings.Count);
        Assert.Equal(0.7, low.Blocks[0].Confidence, 3);
        Assert.Equal(VerificationStatus.Verified, EquationVerifier.Verify(low.Blocks[0].Latex).Status);

        Assert.True(AssistantBoardPlan.TryParse(
            File.ReadAllText(Path.Combine(TeachingEvalRunner.FixtureDirectory, "board_plan_originals.json")),
            out AssistantBoardPlan originals, out error), error);
        Assert.Equal(3, originals.OriginalBlocks.Count);
        Assert.Equal("第一行原文", originals.OriginalBlocks[0].Text);
        Assert.Equal("x^2", originals.OriginalBlocks[1].Latex);
    }

    [Fact]
    public void AdoptionSelection_MatchesFixtureSelectedRatio()
    {
        string path = Path.Combine(TeachingEvalRunner.FixtureDirectory, "board_plan_words.json");
        string json = File.ReadAllText(path);
        Assert.True(AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error), error);

        AssistantBoardPlan selected = plan.SelectBlocks(new[] { "w1" });

        Assert.Single(selected.Blocks);
        Assert.Equal("w1", selected.Blocks[0].Id);
        Assert.Equal(0.5, selected.Blocks.Count / (double)plan.Blocks.Count, 6);
        // 计划级字段在部分采用时原样保留。
        Assert.Equal(plan.Originals, selected.Originals);
        Assert.Equal(plan.Confidence, selected.Confidence, 6);
    }

    private static void AssertBoardPlan(
        string fileName, string[] expectedKinds, double expectedConfidence,
        bool expectedReview, int expectedOriginals)
    {
        string path = Path.Combine(TeachingEvalRunner.FixtureDirectory, fileName);
        string json = File.ReadAllText(path);
        Assert.True(AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error), error);

        Assert.Equal(expectedKinds, plan.Blocks.Select(block => block.Kind));
        Assert.Equal(expectedConfidence, plan.Confidence, 6);
        Assert.Equal(expectedReview, plan.NeedsReview);
        Assert.Equal(expectedOriginals, plan.Originals.Count);
        Assert.False(plan.IsEmpty);
    }
}