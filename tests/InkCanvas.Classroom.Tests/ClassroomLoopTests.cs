using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.Classroom;
using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.Classroom.Tests;

public sealed class ClassroomPresetTests
{
    [Fact]
    public void BuiltIns_ContainFourScenes()
    {
        Assert.Equal(4, ClassroomPresetLibrary.BuiltIns.Count);
        Assert.NotNull(ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Teach));
        Assert.NotNull(ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Practice));
        Assert.NotNull(ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Group));
        Assert.NotNull(ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Summary));
    }

    [Fact]
    public void Plan_TeachMode_OpensGuideAndSpotlight_NotTimer()
    {
        PresetAction action = ClassroomPresetLibrary.Plan(
            ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Teach));

        Assert.Contains(ClassroomTool.AiStepGuide, action.Open);
        Assert.Contains(ClassroomTool.Spotlight, action.Open);
        Assert.Contains(ClassroomTool.SlideNavigator, action.Open);
        Assert.DoesNotContain(ClassroomTool.Countdown, action.Open);
        Assert.Contains(ClassroomTool.Countdown, action.Close);
    }

    [Fact]
    public void Plan_PracticeMode_OpensBoardAndTimer_CollapsesToolkit()
    {
        PresetAction action = ClassroomPresetLibrary.Plan(
            ClassroomPresetLibrary.ForScene(ClassroomSceneKind.Practice));

        Assert.Contains(ClassroomTool.Board, action.Open);
        Assert.Contains(ClassroomTool.Countdown, action.Open);
        Assert.Equal(3, action.CountdownMinutes);
        Assert.True(action.CollapseToolkit);
        Assert.DoesNotContain(ClassroomTool.Spotlight, action.Open);
    }

    [Fact]
    public void Normalize_DedupesToolsAndMovesHiddenConflicts()
    {
        var preset = new ClassroomPreset
        {
            Name = "  我的场景  ",
            Tools = new List<ClassroomTool> { ClassroomTool.Board, ClassroomTool.Board },
            Hidden = new List<ClassroomTool> { ClassroomTool.Board, ClassroomTool.Spotlight },
        };

        ClassroomPreset normalized = ClassroomPresetLibrary.Normalize(preset, "mine");

        Assert.Single(normalized.Tools);
        Assert.Equal("我的场景", normalized.Name);
        Assert.Equal("mine", normalized.Id);
        Assert.DoesNotContain(ClassroomTool.Board, normalized.Hidden);
        Assert.Contains(ClassroomTool.Spotlight, normalized.Hidden);
    }

    [Fact]
    public void Serialize_RoundTripsCustomPresets()
    {
        var custom = new ClassroomPreset
        {
            Id = "mine",
            Name = "我的讲题",
            Scene = ClassroomSceneKind.Teach,
            Tools = new List<ClassroomTool> { ClassroomTool.Board, ClassroomTool.Countdown },
            CountdownMinutes = 8,
        };

        IReadOnlyList<ClassroomPreset> restored =
            ClassroomPresetLibrary.Deserialize(ClassroomPresetLibrary.Serialize(new[] { custom }));

        Assert.Single(restored);
        Assert.Equal("我的讲题", restored[0].Name);
        Assert.Equal(8, restored[0].CountdownMinutes);
    }
}

public sealed class ExerciseFlowTests
{
    private static ProblemContext ContextWithOneRegion()
    {
        var context = new ProblemContext { SlideId = 5, SlideIndex = 2 };
        ProblemContextPolicy.Add(context, new ProblemRegion
        {
            ImageBase64 = "img",
            Anchor = new AssistantRect(10, 10, 110, 90),
            Source = "selection",
        }, nowMs: 1);
        return context;
    }

    [Fact]
    public void BuildRequest_CarriesProblemContextAndVariantPurpose()
    {
        ProblemContext context = ContextWithOneRegion();

        AssistantRequest request = ExerciseFlowPolicy.BuildRequest(
            context, ExerciseDifficulty.Advanced, "sess-1");

        Assert.Equal("variant", request.Purpose);
        Assert.Equal("advanced", request.Difficulty);
        Assert.Equal("sess-1", request.SessionId);
        Assert.Single(request.Images);
        Assert.Equal("img", request.ImageBase64);
        Assert.Equal(AssistantMode.Solve, request.Mode);
    }

    [Fact]
    public void Parse_SplitsTextAnswerAndKnowledgePoint()
    {
        string text = "已知 f(x)=x^2+1，求 f(2)。\n答案：5\n知识点：函数求值";

        VariantQuestionDraft draft = ExerciseFlowPolicy.Parse(
            text, ExerciseDifficulty.Basic, nowMs: 12);

        Assert.Equal(ExerciseDifficulty.Basic, draft.Difficulty);
        Assert.Equal("已知 f(x)=x^2+1，求 f(2)。", draft.Text.Trim());
        Assert.Equal("5", draft.Answer);
        Assert.Equal("函数求值", draft.KnowledgePoint);
        Assert.True(draft.IsValid);
        Assert.Equal(12, draft.CreatedAtMs);
    }

    [Fact]
    public void Parse_NoMarkers_KeepsWholeTextAsQuestion()
    {
        VariantQuestionDraft draft = ExerciseFlowPolicy.Parse(
            "请计算 1+1。", ExerciseDifficulty.Advanced);

        Assert.Equal("请计算 1+1。", draft.Text);
        Assert.Equal("", draft.Answer);
    }

    [Fact]
    public void CanStart_RejectsBlankDraft()
    {
        Assert.False(ExerciseFlowPolicy.CanStart(new VariantQuestionDraft { Text = "  " }));
        Assert.False(ExerciseFlowPolicy.CanStart(null));
        Assert.True(ExerciseFlowPolicy.CanStart(new VariantQuestionDraft { Text = "题目" }));
    }

    [Fact]
    public void Plan_TargetsNewPageAndClampsMinutes()
    {
        var draft = new VariantQuestionDraft { Id = "ex-1", Text = "题目" };

        PracticePlan plan = ExerciseFlowPolicy.Plan(draft, 4, 0);

        Assert.Equal("ex-1", plan.ExerciseId);
        Assert.Equal(4, plan.BoardPageIndex);
        Assert.Equal(1, plan.CountdownMinutes);
        Assert.Equal("practice", plan.SceneId);
    }

    [Fact]
    public void BuildPasteLines_IncludesKnowledgePointAndAnswer()
    {
        var draft = new VariantQuestionDraft
        {
            Text = "题干一\n题干二",
            Answer = "42",
            KnowledgePoint = "方程",
        };

        IReadOnlyList<string> lines = ExerciseFlowPolicy.BuildPasteLines(draft);

        Assert.Equal("知识点：方程", lines[0]);
        Assert.Contains("题干二", lines);
        Assert.Equal("参考答案：42", lines[^1]);
    }
}

public sealed class GroupScoreTests
{
    private static readonly string[] SevenNames =
    {
        "甲", "乙", "丙", "丁", "戊", "己", "庚",
    };

    [Fact]
    public void Split_BySize_LeavesRemainderInLastGroup()
    {
        IReadOnlyList<List<string>> groups = GroupSplitPolicy.Split(
            SevenNames, 3, GroupSplitMode.BySize, seed: 7);

        Assert.Equal(3, groups.Count);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(3, groups[1].Count);
        Assert.Single(groups[2]);
    }

    [Fact]
    public void Split_ByGroupCount_DistributesRemainderToFirstGroups()
    {
        IReadOnlyList<List<string>> groups = GroupSplitPolicy.Split(
            SevenNames, 3, GroupSplitMode.ByGroupCount, seed: 7);

        Assert.Equal(3, groups.Count);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
        Assert.Equal(2, groups[2].Count);
        Assert.Equal(7, groups.Sum(group => group.Count));
    }

    [Fact]
    public void Split_EmptyOrInvalid_ReturnsEmpty()
    {
        Assert.Empty(GroupSplitPolicy.Split(Array.Empty<string>(), 2, GroupSplitMode.BySize));
        Assert.Empty(GroupSplitPolicy.Split(SevenNames, 0, GroupSplitMode.BySize));
        Assert.Empty(GroupSplitPolicy.Split(new[] { " ", "" }, 2, GroupSplitMode.ByGroupCount));
    }

    [Fact]
    public void Service_AddScore_RankingAndRoundReset()
    {
        var service = new GroupScoreService();
        service.ResetGroups(SevenNames, GroupSplitMode.ByGroupCount, 3, seed: 1);

        service.AddScore(1, 5);
        service.AddScore(2, 8);
        service.AddScore(3, -2);

        Assert.Equal(5, service.Groups[0].Score);
        Assert.Equal(8, service.Groups[1].Score);
        Assert.Equal(2, service.Ranking()[0].Index);
        Assert.Equal(11, service.State.TotalScore);
        Assert.Equal(7, service.Groups.Sum(group => group.Members.Count));

        service.ResetRound();
        Assert.Equal(2, service.State.Round);
        Assert.All(service.Groups, group => Assert.Equal(0, group.Score));
        Assert.All(service.Groups, group => Assert.NotEmpty(group.Members));
    }

    [Fact]
    public void Service_MoveMember_KeepsSingleMembership()
    {
        var service = new GroupScoreService();
        service.ResetGroups(SevenNames, GroupSplitMode.ByGroupCount, 2, seed: 3);
        ScoreGroup target = service.Groups[1];
        string moved = service.Groups[0].Members[0];

        Assert.True(service.MoveMember(moved, target.Index));

        Assert.Contains(moved, target.Members);
        Assert.DoesNotContain(moved, service.Groups[0].Members);
        Assert.Equal(7, service.Groups.Sum(group => group.Members.Count));
    }

    [Fact]
    public void Service_Serialize_RoundTripsScores()
    {
        var service = new GroupScoreService();
        service.ResetGroups(SevenNames, GroupSplitMode.BySize, 2, seed: 5);
        service.AddScore(1, 6);

        GroupScoreService restored = GroupScoreService.Deserialize(service.Serialize());

        Assert.Equal(service.Groups.Count, restored.Groups.Count);
        Assert.Equal(6, restored.Groups[0].Score);
        Assert.Contains("小组积分", restored.BuildResultText());
    }
}

public sealed class FocusRegionTests
{
    private static FocusRegionItem Region(int index)
        => new()
        {
            X = index * 10,
            Y = index * 10,
            Width = 100,
            Height = 80,
            Name = "区域 " + index,
        };

    [Fact]
    public void Add_AssignsOrderAndSerializes()
    {
        var store = new FocusRegionStore();

        FocusRegionItem first = store.Add(Region(1), nowMs: 10);
        FocusRegionItem second = store.Add(Region(2), nowMs: 20);

        Assert.Equal(1, first.Order);
        Assert.Equal(2, second.Order);
        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.Equal(20, store.UpdatedAtMs);

        FocusRegionStore restored = FocusRegionStore.Deserialize(store.Serialize());
        Assert.Equal(2, restored.Count);
        Assert.Equal(second.Id, restored.Regions[1].Id);
    }

    [Fact]
    public void Add_RejectsEmptyAndCapsMaximum()
    {
        var store = new FocusRegionStore();
        Assert.Null(store.Add(new FocusRegionItem { Width = 0, Height = 0 }));

        for (int i = 0; i < FocusRegionStore.MaximumRegions; i++)
            Assert.NotNull(store.Add(Region(i)));

        Assert.Null(store.Add(Region(99)));
    }

    [Fact]
    public void Next_CyclesForwardAndBackward()
    {
        var store = new FocusRegionStore();
        FocusRegionItem first = store.Add(Region(1));
        FocusRegionItem second = store.Add(Region(2));

        Assert.Equal(second.Id, store.Next(first, 1).Id);
        Assert.Equal(first.Id, store.Next(second, 1).Id);
        Assert.Equal(second.Id, store.Next(first, -1).Id);
    }

    [Fact]
    public void Move_ReordersAndRemovesReindexes()
    {
        var store = new FocusRegionStore();
        FocusRegionItem first = store.Add(Region(1));
        FocusRegionItem second = store.Add(Region(2));

        Assert.True(store.Move(second.Id, -1));
        Assert.Equal(second.Id, store.Regions[0].Id);
        Assert.Equal(1, second.Order);

        Assert.True(store.Remove(first.Id));
        Assert.Single(store.Regions);
        Assert.Equal(1, store.Regions[0].Order);
    }

    [Fact]
    public void FindMatching_MatchesScreenRect()
    {
        var store = new FocusRegionStore();
        FocusRegionItem item = store.Add(new FocusRegionItem
        {
            X = 100, Y = 200, Width = 300, Height = 200,
        });

        Assert.Equal(item.Id, store.FindMatching(101, 199, 299, 201).Id);
        Assert.Null(store.FindMatching(0, 0, 10, 10));
    }
}

public sealed class HandoutExporterTests
{
    private static HandoutPageSource Page(int id, int index, bool annotated, bool notes)
        => new()
        {
            SlideId = id,
            Index = index,
            Title = "第" + index + "页",
            SlideImagePng = new byte[] { 1, 2, 3 },
            AnnotationMetadataJson = annotated ? "{\"text\":\"批注\"}" : "",
            TeacherNotes = notes ? "教师解析" : "",
        };

    [Fact]
    public void Select_AllPages_KeepsOrder()
    {
        var pages = new[]
        {
            Page(11, 1, false, false),
            Page(12, 2, true, false),
            Page(13, 3, false, true),
        };

        IReadOnlyList<HandoutPageSource> selected = HandoutComposer.Select(
            pages, new HandoutOptions());

        Assert.Equal(3, selected.Count);
        Assert.Equal(new[] { 11, 12, 13 }, selected.Select(page => page.SlideId));
    }

    [Fact]
    public void Select_AnnotationsOnly_FiltersUnannotated()
    {
        var pages = new[]
        {
            Page(11, 1, false, false),
            Page(12, 2, true, false),
        };

        IReadOnlyList<HandoutPageSource> selected = HandoutComposer.Select(
            pages, new HandoutOptions { Content = HandoutContentMode.AnnotationsOnly });

        Assert.Single(selected);
        Assert.Equal(12, selected[0].SlideId);
    }

    [Fact]
    public void Select_SelectedIds_LimitsOutput()
    {
        var pages = new[]
        {
            Page(11, 1, true, false),
            Page(12, 2, true, false),
            Page(13, 3, true, false),
        };

        IReadOnlyList<HandoutPageSource> selected = HandoutComposer.Select(
            pages, new HandoutOptions { SelectedSlideIds = new List<int> { 13, 11 } });

        Assert.Equal(2, selected.Count);
        Assert.Equal(new[] { 11, 13 }, selected.Select(page => page.SlideId));
    }

    [Fact]
    public void PageSize_InvalidFallbackToA4()
    {
        (double width, double height) = HandoutComposer.PageSize(
            new HandoutOptions { PageWidthPt = 10, PageHeightPt = 0 });

        Assert.Equal(595.28, width);
        Assert.Equal(841.89, height);
    }

    [Fact]
    public void PdfBuilder_OnePage_HasCatalogPagesContentAndEof()
    {
        var rgb = new byte[2 * 2 * 3];
        byte[] pdf = HandoutPdfBuilder.Build(new[] { new HandoutPdfPage(2, 2, rgb) });

        string text = Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/Count 1", text);
        Assert.Contains("/Filter /FlateDecode", text);
        Assert.Contains("/Im0 Do", text);
        Assert.Contains("xref", text);
        Assert.EndsWith("%%EOF\n", text);
    }

    [Fact]
    public void PdfBuilder_MultiplePages_CountMatches()
    {
        var rgb = new byte[1 * 1 * 3];
        byte[] pdf = HandoutPdfBuilder.Build(new[]
        {
            new HandoutPdfPage(1, 1, rgb),
            new HandoutPdfPage(1, 1, rgb),
        });

        string text = Encoding.ASCII.GetString(pdf);

        Assert.Contains("/Count 2", text);
        Assert.Equal(2, CountOccurrences(text, "/Type /Page "));
    }

    [Fact]
    public void PdfBuilder_Empty_ReturnsEmpty()
    {
        Assert.Empty(HandoutPdfBuilder.Build(Array.Empty<HandoutPdfPage>()));
        Assert.Empty(HandoutPdfBuilder.Build(new[] { new HandoutPdfPage(2, 2, new byte[1]) }));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}