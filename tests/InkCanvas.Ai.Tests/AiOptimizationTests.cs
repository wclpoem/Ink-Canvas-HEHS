using System;
using System.Collections.Generic;
using System.Text.Json;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Platform.Ai;
using Xunit;

namespace InkCanvas.Ai.Tests;

[Collection("AiConfigEnvironment")]
public sealed class AiOptimizationTests
{
    // ---------- 局部纠错 ----------

    private static AssistantSession SessionWithText(string text)
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Solve, "session-1");
        session.State = AssistantState.Streaming;
        session.Text = text;
        session.Formula = "x^2-5x+6";
        session.Steps.Add(new AssistantStep { Index = 0, Text = "因式分解", Latex = "(x-2)(x-3)=0" });
        session.Steps.Add(new AssistantStep { Index = 1, Text = "求根", Latex = "x=2,x=3" });
        session.Annotations.Add(new List<IReadOnlyList<double>> { new List<double> { 1, 2 } });
        session.Regions.Add(new AssistantRegion
        {
            TextStart = 0,
            TextLength = 2,
            Rect = new AssistantRect(0, 0, 10, 10),
            Source = "local",
        });
        session.TokenCount = 9;
        session.Confidence = 0.8;
        session.Verified = false;
        session.LocalHint = "本地提示";
        session.Error = "";
        session.StartedAtMs = 100;
        session.FirstTokenAtMs = 200;
        return session;
    }

    [Fact]
    public void Replace_Middle_ReplacesOnlyTargetRange()
    {
        var session = SessionWithText("hello world");

        var updated = AssistantReplace.ReplaceText(session, 6, 5, "there");

        Assert.Equal("hello there", updated.Text);
        Assert.Equal("hello world", session.Text);
        Assert.NotSame(session, updated);
    }

    [Fact]
    public void Replace_Start_PrependsReplacement()
    {
        var session = SessionWithText("hello world");

        var updated = AssistantReplace.ReplaceText(session, 0, 5, "hi");

        Assert.Equal("hi world", updated.Text);
    }

    [Fact]
    public void Replace_End_ReplacesTail()
    {
        var session = SessionWithText("hello world");

        var updated = AssistantReplace.ReplaceText(session, 6, 5, "there");

        Assert.Equal("hello there", updated.Text);
    }

    [Fact]
    public void Replace_ClampsOutOfRange()
    {
        var session = SessionWithText("abc");

        Assert.Equal("X", AssistantReplace.ReplaceText(session, -10, 100, "X").Text);
        Assert.Equal("abcX", AssistantReplace.ReplaceText(session, 3, 100, "X").Text);
        Assert.Equal("Xabc", AssistantReplace.ReplaceText(session, 0, 0, "X").Text);
        Assert.Equal("abcX", AssistantReplace.ReplaceText(session, 10, 10, "X").Text);
        Assert.Equal("abc", AssistantReplace.ReplaceText(session, 1, -5, "").Text);
    }

    [Fact]
    public void Replace_KeepsStepsFormulaAndSessionState()
    {
        var session = SessionWithText("answer");

        var updated = AssistantReplace.ReplaceText(session, 0, 6, "fixed");

        Assert.Equal("fixed", updated.Text);
        Assert.Equal(session.Formula, updated.Formula);
        Assert.Equal(2, updated.Steps.Count);
        Assert.Equal("(x-2)(x-3)=0", updated.Steps[0].Latex);
        Assert.Single(updated.Annotations);
        Assert.Single(updated.Regions);
        Assert.Equal(AssistantState.Streaming, updated.State);
        Assert.Equal(9, updated.TokenCount);
        Assert.Equal(0.8, updated.Confidence);
        Assert.False(updated.Verified);
        Assert.Equal("本地提示", updated.LocalHint);
        Assert.Equal(100, updated.StartedAtMs);
        Assert.Equal(200, updated.FirstTokenAtMs);
        Assert.Equal(AssistantState.Streaming, session.State);
        Assert.Equal(9, session.TokenCount);
    }

    [Fact]
    public void ApplyRegionResult_UsesRegionOffsets()
    {
        var session = SessionWithText("x = 2");
        var region = new AssistantRegion { TextStart = 4, TextLength = 1, Source = "cloud" };

        var updated = AssistantReplace.ApplyRegionResult(session, region, "3");

        Assert.Equal("x = 3", updated.Text);
        Assert.Null(AssistantReplace.ApplyRegionResult(null, region, "3"));
        Assert.Equal("x = 2", AssistantReplace.ApplyRegionResult(session, null, "3").Text);
    }

    // ---------- 事件置信度/自检 ----------

    [Fact]
    public void ApplyEvent_Formula_SyncsConfidenceAndVerified()
    {
        var session = SessionWithText("");

        AssistantMachine.ApplyEvent(
            session,
            new AssistantEvent
            {
                Kind = AssistantEventKind.Formula,
                Latex = "x=3",
                Confidence = 0.92,
                Verified = true,
            },
            1000);

        Assert.Equal("x=3", session.Formula);
        Assert.Equal(0.92, session.Confidence);
        Assert.True(session.Verified);
    }

    [Fact]
    public void ApplyEvent_Formula_KeepsConfidence_WhenEventHasNone()
    {
        var session = SessionWithText("");
        session.Confidence = 0.7;

        AssistantMachine.ApplyEvent(
            session,
            new AssistantEvent { Kind = AssistantEventKind.Formula, Latex = "x=4", Confidence = -1 },
            1000);

        Assert.Equal(0.7, session.Confidence);
    }

    // ---------- 方程自检 ----------

    [Fact]
    public void Verifier_SimpleIdentity_IsVerified()
    {
        var result = EquationVerifier.Verify("x = 3");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Contains("x=3", result.Note);
        Assert.Contains("成立", result.Note);
    }

    [Fact]
    public void Verifier_LinearEquation_IsVerified()
    {
        var result = EquationVerifier.Verify("3x + 5 = 20");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Contains("x=5", result.Note);
    }

    [Fact]
    public void Verifier_Quadratic_TwoRoots_AreVerified()
    {
        var result = EquationVerifier.Verify("x^2 - 5x + 6 = 0");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Contains("x=2", result.Note);
        Assert.Contains("x=3", result.Note);
    }

    [Fact]
    public void Verifier_QuadraticVariant_WithLeadingCoefficient_IsVerified()
    {
        var result = EquationVerifier.Verify("2x^2 - 4x - 6 = 0");

        Assert.Equal(VerificationStatus.Verified, result.Status);
        Assert.Contains("x=3", result.Note);
        Assert.Contains("x=-1", result.Note);
    }

    [Fact]
    public void Verifier_Quadratic_WithoutRealRoots_IsUnverified()
    {
        var result = EquationVerifier.Verify("x^2 + 1 = 0");

        Assert.Equal(VerificationStatus.Unverified, result.Status);
        Assert.Contains("无实根", result.Note);
    }

    [Fact]
    public void Verifier_GarbageText_IsUnverified()
    {
        var result = EquationVerifier.Verify("请帮我看看这道题");

        Assert.Equal(VerificationStatus.Unverified, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Note));
    }

    [Fact]
    public void Verifier_PlainFormula_IsUnverifiedWithoutEquals()
    {
        var result = EquationVerifier.Verify("3x + 5");

        Assert.Equal(VerificationStatus.Unverified, result.Status);
    }

    [Fact]
    public void Verifier_LowConfidence_IsFlagged()
    {
        var low = EquationVerifier.Verify("x = 3", 0.3);

        Assert.Equal(VerificationStatus.LowConfidence, low.Status);
        Assert.Contains("0.5", low.Note);

        var ok = EquationVerifier.Verify("x = 3", 0.9);
        Assert.Equal(VerificationStatus.Verified, ok.Status);
    }

    [Fact]
    public void Verifier_NeverThrows_OnOddInput()
    {
        Assert.Equal(VerificationStatus.Unverified, EquationVerifier.Verify(null).Status);
        Assert.Equal(VerificationStatus.Unverified, EquationVerifier.Verify("").Status);
        Assert.Equal(VerificationStatus.Unverified, EquationVerifier.Verify("x = ").Status);
        Assert.Equal(VerificationStatus.Unverified, EquationVerifier.Verify("x = 3 = 4").Status);
    }

    // ---------- 两阶段请求 ----------

    private static AssistantRequest Request(string image)
    {
        return new AssistantRequest
        {
            ImageBase64 = image,
            Mode = AssistantMode.Formula,
            SessionId = "s-1",
            Viewport = new AssistantViewport { Scale = 2.0, Rect = new AssistantRect(1, 2, 3, 4) },
            InkStrokes = new List<AssistantStroke> { new() },
        };
    }

    [Fact]
    public void Payload_InkOnly_OmitsImage()
    {
        var request = Request("BASE64");
        request.Stage = AssistantStage.InkOnly;

        using var document = JsonDocument.Parse(AiClient.BuildPayload(request, "m1"));
        var root = document.RootElement;

        Assert.Equal("", root.GetProperty("imageBase64").GetString());
        Assert.Equal("inkOnly", root.GetProperty("stage").GetString());
        Assert.False(root.TryGetProperty("region", out _));
        Assert.Equal("m1", root.GetProperty("model").GetString());
        Assert.Equal("formula", root.GetProperty("mode").GetString());
        Assert.Equal("s-1", root.GetProperty("sessionId").GetString());
        Assert.Equal(1, root.GetProperty("inkStrokes").GetArrayLength());
        Assert.Equal(2.0, root.GetProperty("viewport").GetProperty("scale").GetDouble());
    }

    [Fact]
    public void Payload_WithImage_IncludesImage()
    {
        var request = Request("BASE64");
        request.Stage = AssistantStage.WithImage;

        using var document = JsonDocument.Parse(AiClient.BuildPayload(request, "m1"));
        var root = document.RootElement;

        Assert.Equal("BASE64", root.GetProperty("imageBase64").GetString());
        Assert.Equal("withImage", root.GetProperty("stage").GetString());
        Assert.False(root.TryGetProperty("region", out _));
    }

    [Fact]
    public void Payload_Region_IsWrittenWhenProvided()
    {
        var request = Request("BASE64");
        request.Stage = AssistantStage.InkOnly;
        request.Region = new AssistantRegion
        {
            Rect = new AssistantRect(10, 20, 30, 40),
            TextStart = 5,
            TextLength = 6,
            Source = "local",
        };

        using var document = JsonDocument.Parse(AiClient.BuildPayload(request, "m1"));
        var region = document.RootElement.GetProperty("region");

        Assert.Equal(5, region.GetProperty("textStart").GetInt32());
        Assert.Equal(6, region.GetProperty("textLength").GetInt32());
        Assert.Equal(10, region.GetProperty("rect").GetProperty("left").GetDouble());
        Assert.Equal(40, region.GetProperty("rect").GetProperty("bottom").GetDouble());
    }

    [Fact]
    public void Payload_LegacySixFields_ArePresent()
    {
        using var document = JsonDocument.Parse(AiClient.BuildPayload(Request(""), ""));
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("model", out _));
        Assert.True(root.TryGetProperty("mode", out _));
        Assert.True(root.TryGetProperty("sessionId", out _));
        Assert.True(root.TryGetProperty("imageBase64", out _));
        Assert.True(root.TryGetProperty("inkStrokes", out _));
        Assert.True(root.TryGetProperty("viewport", out _));
        Assert.Equal(AiConfig.DefaultModel, root.GetProperty("model").GetString());
        Assert.Equal("withImage", root.GetProperty("stage").GetString());
    }

    [Fact]
    public void Payload_ChatIncludesTheActualFollowUpQuestion()
    {
        var request = new AssistantRequest { Mode = AssistantMode.Chat, Question = "为什么可以分解成这两个因式？" };
        using var document = JsonDocument.Parse(AiClient.BuildPayload(request, "m1"));
        Assert.Equal(request.Question, document.RootElement.GetProperty("question").GetString());
    }

    // ---------- 超时选择 ----------

    [Fact]
    public void Config_SelectsTimeoutByStage()
    {
        var config = new AiConfig { TimeoutMs = 5000, ImageTimeoutMs = 25000 };

        Assert.Equal(5000, config.TimeoutForStage(AssistantStage.InkOnly));
        Assert.Equal(25000, config.TimeoutForStage(AssistantStage.WithImage));

    }

    [Fact]
    public void Config_ImageTimeoutDefault_IsTwentySeconds()
    {
        Assert.Equal(20000, new AiConfig().ImageTimeoutMs);
        Assert.Equal(20000, AiConfig.DefaultImageTimeoutMs);
    }

    [Fact]
    public void Config_ImageTimeoutEnvironment_IsClamped()
    {
        string previous = Environment.GetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS");
        try
        {
            Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", "999999");
            Assert.Equal(AiConfig.MaxTimeoutMs, AiConfig.ReadFromEnvironment().ImageTimeoutMs);

            Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", "50");
            Assert.Equal(AiConfig.MinTimeoutMs, AiConfig.ReadFromEnvironment().ImageTimeoutMs);

            Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", "30000");
            Assert.Equal(30000, AiConfig.ReadFromEnvironment().ImageTimeoutMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_IMAGE_TIMEOUT_MS", previous);
        }
    }

    // ---------- 弱网本地提示 ----------

    [Fact]
    public void Transcriber_WithFormula_ReturnsPlainText()
    {
        var session = new AssistantSession { Formula = "x^2+1" };

        Assert.Equal("x²+1", LocalFormulaTranscriber.Hint(Array.Empty<AssistantStroke>(), session));
    }

    [Fact]
    public void Transcriber_WithoutFormula_ReportsStrokeCount()
    {
        var strokes = new List<AssistantStroke> { new(), new(), new() };

        Assert.Equal(
            "本地已识别到 3 笔，正在云端转写…",
            LocalFormulaTranscriber.Hint(strokes, new AssistantSession()));
        Assert.Equal(
            "本地已识别到 0 笔，正在云端转写…",
            LocalFormulaTranscriber.Hint(null, null));
    }
}
