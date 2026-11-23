using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Ppt;
using InkCanvas.Ink;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 阶段 3A 闭环：例题关联字段往返/旧文件兼容、再讲与贴回只读原题、
/// 总结块不降级、总结页一次撤销、教师订正来源、讲义预览页数。
/// </summary>
public sealed class ExampleSummaryClosureTests : IDisposable
{
    private readonly string _root;

    public ExampleSummaryClosureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-stage3a", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 清理失败不影响断言 */ }
    }

    [Fact]
    public void ExampleEntry_AiAssociation_RoundTrips()
    {
        string directory = Path.Combine(_root, "roundtrip");
        var entry = new ExampleEntry
        {
            Id = "ex-ai-1",
            Title = "AI 关联例题",
            Answer = "x = 2",
            CorrectedText = "注意定义域",
            SourcePresentation = "3.2 一次函数.pptx",
            SourceSlideId = 7,
            BoardPage = 2,
            CreatedAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            AiSessionId = "sess-001",
            RequestId = "req-001",
            BoardId = "main",
            BoardPageId = Guid.NewGuid(),
            GroupId = "group-001",
            ObjectIds = new List<string> { "oid-1", "oid-2" },
            StepsJson = ExampleEntryCodec.EncodeSteps(new List<AssistantStep>
            {
                new() { Index = 1, Text = "去分母" },
                new() { Index = 2, Latex = "x=2" },
            }),
            AnnotationsJson = ExampleEntryCodec.EncodeAnnotations(new List<IReadOnlyList<IReadOnlyList<double>>>
            {
                new List<IReadOnlyList<double>> { new List<double> { 1, 2 }, new List<double> { 3, 4 } },
            }),
            TeacherEdited = true,
            UpdatedAt = new DateTime(2026, 6, 2, 10, 30, 0, DateTimeKind.Utc),
        };

        Assert.True(ExampleLibraryStore.Add(entry, directory));
        ExampleEntry loaded = ExampleLibraryStore.Load(directory).Single(item => item.Id == entry.Id);

        Assert.Equal(entry.AiSessionId, loaded.AiSessionId);
        Assert.Equal(entry.RequestId, loaded.RequestId);
        Assert.Equal(entry.BoardId, loaded.BoardId);
        Assert.Equal(entry.BoardPageId, loaded.BoardPageId);
        Assert.Equal(entry.GroupId, loaded.GroupId);
        Assert.Equal(entry.ObjectIds, loaded.ObjectIds);
        Assert.Equal(entry.StepsJson, loaded.StepsJson);
        Assert.Equal(entry.AnnotationsJson, loaded.AnnotationsJson);
        Assert.True(loaded.TeacherEdited);
        Assert.Equal(entry.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public void ExampleEntry_OldFile_DefaultsOptionalFields()
    {
        string directory = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(directory);
        File.WriteAllText(ExampleLibraryStore.PathFor(directory), """
            {
              "version": 1,
              "examples": [
                {
                  "id": "ex-old",
                  "title": "旧例题",
                  "answer": "A",
                  "sourcePresentation": "旧课件.pptx",
                  "sourceSlideId": 3,
                  "boardPage": 1,
                  "createdAt": "2026-01-01T00:00:00Z"
                }
              ]
            }
            """);

        ExampleEntry entry = ExampleLibraryStore.Load(directory).Single();

        Assert.Equal("", entry.AiSessionId);
        Assert.Equal("", entry.RequestId);
        Assert.Equal("", entry.BoardId);
        Assert.Equal(Guid.Empty, entry.BoardPageId);
        Assert.Equal("", entry.GroupId);
        Assert.Empty(entry.ObjectIds);
        Assert.Equal("", entry.StepsJson);
        Assert.Equal("", entry.AnnotationsJson);
        Assert.False(entry.TeacherEdited);
        Assert.Equal(default, entry.UpdatedAt);
    }

    [Fact]
    public void ReExplainAndPaste_DoNotMutateSourceExample()
    {
        var entry = new ExampleEntry
        {
            Id = "ex-readonly",
            Title = "只读例题",
            Answer = "答案 A",
            CorrectedText = "订正 B",
            SourcePresentation = "只读.pptx",
            SourceSlideId = 5,
            BoardPage = 1,
            ObjectIds = new List<string> { "oid-1" },
            StepsJson = ExampleEntryCodec.EncodeSteps(new List<AssistantStep>
            {
                new() { Index = 1, Text = "第一步" },
                new() { Index = 2, Text = "第二步" },
            }),
        };

        string before = ExampleEntryCodec.EncodeSteps(ExampleEntryCodec.DecodeSteps(entry.StepsJson));
        string prompt = ExampleReExplainPrompt.Build(entry);
        AssistantBoardPlan plan = ExampleBoardPlanFactory.Create(entry);
        List<string> lines = ExampleBoardPlanFactory.LegacyLines(entry);

        Assert.Contains("只读例题", prompt);
        Assert.Contains("答案 A", prompt);
        Assert.Contains("订正 B", prompt);
        Assert.Equal(2, plan.Blocks.Count);
        Assert.NotEmpty(lines);
        Assert.Equal("ex-readonly", entry.Id);
        Assert.Equal("只读例题", entry.Title);
        Assert.Equal("答案 A", entry.Answer);
        Assert.Equal("订正 B", entry.CorrectedText);
        Assert.Equal(1, entry.ObjectIds.Count);
        Assert.Equal(before, entry.StepsJson);
    }

    [Fact]
    public void SummaryBlock_IsNotDowngradedToText()
    {
        const string json = """
            {
              "schema": 1,
              "intent": "summary",
              "blocks": [ { "id": "s1", "kind": "summary", "text": "本课回顾：一次函数图象" } ]
            }
            """;

        Assert.True(AssistantBoardPlan.TryParse(json, out AssistantBoardPlan plan, out string error));
        Assert.Equal("", error);
        Assert.Equal("summary", plan.Blocks[0].Kind);
        Assert.True(SummaryBoardPolicy.IsSummary(plan, ""));

        BoardPlanNativeResult created = BoardPlanWriter.Create(plan, "写一句本课总结");
        Assert.True(created.Success);
        Assert.Single(created.Texts);
        Assert.Equal("summary", created.Texts[0].Type);
        Assert.Equal("本课回顾：一次函数图象", created.Texts[0].Content);
    }

    [Fact]
    public void SummaryPage_RetainsPageAfterSingleUndo()
    {
        var board = new BoardService();
        int pagesBefore = board.PageCount;

        Assert.True(board.InsertPageAfterCurrent());
        int summaryIndex = board.CurrentIndex;
        Assert.True(board.RenamePage(summaryIndex, SummaryBoardPolicy.ResolvePageName("课堂总结")));
        Assert.Equal("课堂总结", board.PageName(summaryIndex));

        var text = new BoardTextObject
        {
            Type = "summary",
            Content = "本课总结",
            FontFamily = "Microsoft YaHei UI",
            FontSize = 28,
            Color = "#FF202024",
            LineHeight = 1.4,
            Align = "left",
            Left = 20,
            Top = 20,
            Width = 360,
            Height = 160,
            Source = "ai",
        };
        IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
            new[] { text }, Array.Empty<BoardFormulaObject>(), "AI 板书（新页）", "summary-group");
        Assert.Single(ids);
        Assert.Single(board.CurrentTextObjects);

        board.Current.Undo();

        Assert.Equal(pagesBefore + 1, board.PageCount);
        Assert.Equal("课堂总结", board.PageName(board.CurrentIndex));
        Assert.Empty(board.CurrentTextObjects);
    }

    [Fact]
    public void TeacherEdit_MarksSourceAndInstruction()
    {
        var text = new BoardTextObject
        {
            Type = "text",
            Content = "AI 原文",
            Left = 10,
            Top = 10,
            Width = 300,
            Height = 120,
            Source = "ai",
            Instruction = "写一句总结",
        };
        var formula = new BoardFormulaObject
        {
            Latex = "x^2",
            Left = 10,
            Top = 10,
            Width = 200,
            Height = 80,
            Source = "ai",
        };

        AiCardEditState.MarkTeacherEdited(text);
        AiCardEditState.MarkTeacherEdited(formula);

        Assert.Equal(BoardObjectProvenance.TeacherSource, text.Source);
        Assert.StartsWith(BoardObjectProvenance.TeacherInstructionPrefix, text.Instruction);
        Assert.Equal(ObjectSource.TeacherEdit, BoardObjectProvenance.FromLegacy(text.Source).Source);
        Assert.Equal(BoardObjectProvenance.TeacherSource, formula.Source);
        Assert.StartsWith(BoardObjectProvenance.TeacherInstructionPrefix, formula.Instruction);
    }

    [Fact]
    public void HandoutPreview_CountsPptBoardAndSummary()
    {
        var index = new SlideContentIndex
        {
            Slides = new List<SlideContentEntry>
            {
                new() { SlideId = 1, Index = 1, Name = "第一页", HasInk = true },
                new() { SlideId = 2, Index = 2, Name = "第二页", HasInk = false },
            },
        };
        var boardPages = new List<HandoutComposer.BoardPageInfo>
        {
            new(0, true, false),
            new(1, true, true),
            new(2, false, false),
        };

        HandoutComposer.PreviewSummary all = HandoutComposer.Summarize(
            index, new HandoutOptions { Content = HandoutContentMode.AnnotationsWithTeacherNotes }, boardPages);
        Assert.Equal(2, all.PresentationPages);
        Assert.Equal(2, all.BoardPages);
        Assert.Equal(1, all.SummaryPages);
        Assert.Equal(4, all.TotalPages);
        Assert.Contains("总结 1 页", all.Describe());

        HandoutComposer.PreviewSummary annotationsOnly = HandoutComposer.Summarize(
            index, new HandoutOptions { Content = HandoutContentMode.AnnotationsOnly }, boardPages);
        Assert.Equal(1, annotationsOnly.PresentationPages);
        Assert.Equal(3, annotationsOnly.TotalPages);
    }

    [Fact]
    public void ExampleEntryCodec_ToleratesMalformedJson()
    {
        Assert.Empty(ExampleEntryCodec.DecodeSteps("{ not json"));
        Assert.Empty(ExampleEntryCodec.DecodeSteps(""));
        Assert.Empty(ExampleEntryCodec.DecodeAnnotations("[1,2,3]"));
        Assert.Equal("", ExampleEntryCodec.EncodeSteps(Array.Empty<AssistantStep>()));
    }
}