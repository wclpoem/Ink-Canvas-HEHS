using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Modules.AiAssistant;
using InkCanvas.Platform.Ai;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AssistantConversationTests
{
    [Fact]
    public void EarlierAnswerOwnsItsStepsAndSourceAfterTheNextRequestChanges()
    {
        var session = new AssistantSession { Text = "原题", Steps = new() { new() { Index = 1, Text = "第一步" } },
            Annotations = new() { new IReadOnlyList<double>[] { new double[] { 1, 2 } } } };
        var turn = new AssistantTurn("为什么？", new(10, 20, 30, 40), "第一课", 12, 3);
        turn.Update(session);
        session.Text = "下一题"; session.Steps[0].Text = "下一步";
        session.Annotations.Clear();
        Assert.Equal("原题", turn.Session.Text);
        Assert.Equal("第一步", turn.Session.Steps[0].Text);
        Assert.Single(turn.Session.Annotations);
        Assert.Equal("第一课", turn.Presentation);
        Assert.Equal(12, turn.SlideId); Assert.Equal(3, turn.BoardPage);
    }

    [Theory]
    [InlineData(AssistantTeachingMode.StepByStep, 0, false, false)]
    [InlineData(AssistantTeachingMode.StepByStep, 1, true, false)]
    [InlineData(AssistantTeachingMode.StepByStep, 2, true, true)]
    [InlineData(AssistantTeachingMode.Hint, 2, false, false)]
    [InlineData(AssistantTeachingMode.Full, 0, true, true)]
    public void CopyPasteAndConversationRespectRevealedContent(AssistantTeachingMode mode, int revealed, bool first, bool answer)
    {
        var session = new AssistantSession { Text = "提示", Formula = "最终答案", TeachingMode = mode, RevealedSteps = revealed,
            Steps = new() { new() { Text = "第一步" }, new() { Text = "第二步" } } };
        string visible = AssistantTurn.VisibleText(session);
        Assert.Contains("提示", visible);
        Assert.Equal(first, visible.Contains("第一步", StringComparison.Ordinal));
        Assert.Equal(answer, visible.Contains("最终答案", StringComparison.Ordinal));
    }

    [Fact]
    public void RevealingMoreContentMakesAnEarlierPasteAvailableAgain()
    {
        var session = new AssistantSession { Text = "题目", RevealedSteps = 0,
            Steps = new() { new() { Text = "推导" } } };
        var turn = new AssistantTurn("", default, "", 0, -1);
        turn.Update(session); turn.Pasted = true;
        turn.Update(session); Assert.True(turn.Pasted);
        session.RevealedSteps = 1;
        turn.Update(session); Assert.False(turn.Pasted);
    }

    [Fact]
    public void FollowUpPayloadCarriesOrderedConversationAndTheNewQuestion()
    {
        var request = new AssistantRequest { Question = "再解释一次", Messages = new AssistantMessage[] {
            new("user", "如何解方程"), new("assistant", "先因式分解") } };
        using var document = JsonDocument.Parse(AiClient.BuildPayload(request, "model"));
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("先因式分解", messages[1].GetProperty("content").GetString());
        Assert.Equal("再解释一次", document.RootElement.GetProperty("question").GetString());
    }
}
