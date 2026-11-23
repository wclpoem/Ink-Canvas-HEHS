using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>Editable AI paste cards: metadata round-trip, persistence and image re-rendering.</summary>
public sealed class AiPasteMetadataTests
{
    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-aipaste-{Guid.NewGuid():N}-{name}");

    private static BoardImageAsset NewImage(byte fill = 1) => new()
    {
        Name = "AI 助教",
        Data = new[] { fill, (byte)2, (byte)3 },
        ContentType = "image/png",
        Width = 20,
        Height = 10,
        Left = 5,
        Top = 6,
    };

    [Fact]
    public void Serialize_RoundTripsFormulaMetadata()
    {
        var metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.Formula,
            Latex = "\\frac{1}{2}",
            Annotations = new List<List<double>> { new() { 1, 2 }, new() { 3, 4 } },
        };

        string json = metadata.Serialize();

        Assert.Contains("\"latex\"", json);
        Assert.Contains("\"kind\"", json);
        var parsed = AiPasteMetadata.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Equal(AiPasteMetadata.CurrentVersion, parsed.Version);
        Assert.Equal(AiPasteKind.Formula, parsed.Kind);
        Assert.Equal("\\frac{1}{2}", parsed.Latex);
        Assert.Equal(2, parsed.Annotations.Count);
        Assert.Equal(3d, parsed.Annotations[1][0], 6);
    }

    [Fact]
    public void Serialize_RoundTripsStepsAndShapeMetadata()
    {
        var steps = new AiPasteMetadata
        {
            Kind = AiPasteKind.Steps,
            Text = "第一步\n第二步",
            Lines = new List<string> { "第一步", "第二步" },
        };
        var parsedSteps = AiPasteMetadata.TryParse(steps.Serialize());
        Assert.NotNull(parsedSteps);
        Assert.Equal(AiPasteKind.Steps, parsedSteps.Kind);
        Assert.Equal(2, parsedSteps.Lines.Count);
        Assert.Equal("第二步", parsedSteps.Lines[1]);

        var shape = new AiPasteMetadata { Kind = AiPasteKind.Shape, Text = "三角形的高" };
        var parsedShape = AiPasteMetadata.TryParse(shape.Serialize());
        Assert.NotNull(parsedShape);
        Assert.Equal(AiPasteKind.Shape, parsedShape.Kind);
        Assert.Equal("三角形的高", parsedShape.Text);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        Assert.Null(AiPasteMetadata.TryParse("{\"latex\":"));
        Assert.Null(AiPasteMetadata.TryParse("not json at all"));
        Assert.Null(AiPasteMetadata.TryParse("null"));
        Assert.Null(AiPasteMetadata.TryParse("[]"));
    }

    [Fact]
    public void TryParse_EmptyInput_ReturnsNull()
    {
        Assert.Null(AiPasteMetadata.TryParse(""));
        Assert.Null(AiPasteMetadata.TryParse("   "));
        Assert.Null(AiPasteMetadata.TryParse(null));
    }

    [Fact]
    public void TryParse_MissingVersion_DefaultsToCurrentVersion()
    {
        var parsed = AiPasteMetadata.TryParse("{\"kind\":1,\"text\":\"x\"}");
        Assert.NotNull(parsed);
        Assert.Equal(AiPasteMetadata.CurrentVersion, parsed.Version);
        Assert.Equal(AiPasteKind.Steps, parsed.Kind);
    }

    [Fact]
    public void TryParse_WithExplicitVersion_PreservesIt()
    {
        var parsed = AiPasteMetadata.TryParse("{\"version\":7,\"kind\":2,\"text\":\"x\"}");
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed.Version);
        Assert.Equal(AiPasteKind.Shape, parsed.Kind);
    }

    [Fact]
    public void BoardImageAssetCopy_ClonesMetadataIndependently()
    {
        string json = new AiPasteMetadata { Kind = AiPasteKind.Formula, Latex = "a+b" }.Serialize();
        var image = NewImage();
        image.Metadata = json;

        var copy = image.Copy();

        Assert.Equal(json, copy.Metadata);
        copy.Metadata = "";
        Assert.Equal(json, image.Metadata);
    }

    [Fact]
    public void SaveLoadPackage_PreservesImageMetadata()
    {
        var path = TempFile("board-v3.icink");
        string json = new AiPasteMetadata { Kind = AiPasteKind.Formula, Latex = "x^2" }.Serialize();
        try
        {
            var board = new BoardService();
            var image = NewImage();
            image.Metadata = json;
            Assert.True(board.AddImage(image));

            board.SaveBoard(path);

Assert.Contains("\"Version\": 4", File.ReadAllText(path));

            using (var package = System.IO.Compression.ZipFile.OpenRead(path))
            using (var manifest = package.GetEntry("board.json")!.Open())
            using (var jsonDocument = System.Text.Json.JsonDocument.Parse(manifest))
                Assert.Equal(4, jsonDocument.RootElement.GetProperty("Version").GetInt32());

            var loaded = new BoardService();
            loaded.LoadBoard(path);

            Assert.Single(loaded.CurrentImages);
            Assert.Equal(json, loaded.CurrentImages[0].Metadata);
            Assert.Equal(image.Data, loaded.CurrentImages[0].Data);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadLegacyV2Board_ImageMetadataDefaultsToEmpty()
    {
        var path = TempFile("board-v2.icink");
        string data = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        string json =
            "{\"Format\":\"inkcanvas-board\",\"Version\":2,\"Background\":\"White\",\"GridSpacing\":32," +
            "\"Pages\":[{\"Name\":\"旧页面\",\"Strokes\":[],\"Images\":[" +
            "{\"Name\":\"旧图片\",\"Data\":\"" + data + "\",\"ContentType\":\"image/png\"," +
            "\"Left\":1,\"Top\":2,\"Width\":20,\"Height\":10}]}]}";
        try
        {
            File.WriteAllText(path, json);

            var board = new BoardService();
            board.LoadBoard(path);

            Assert.Equal(1, board.PageCount);
            Assert.Single(board.CurrentImages);
            Assert.Equal("", board.CurrentImages[0].Metadata);
            Assert.Equal(20f, board.CurrentImages[0].Width, 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void UpdateImage_ReplacesDataAndMetadataKeepingGeometry()
    {
        var board = new BoardService();
        var image = NewImage();
        string before = new AiPasteMetadata { Kind = AiPasteKind.Formula, Latex = "a" }.Serialize();
        image.Metadata = before;
        Assert.True(board.AddImage(image));

        string after = new AiPasteMetadata { Kind = AiPasteKind.Formula, Latex = "b" }.Serialize();
        byte[] newData = { 9, 9, 9, 9 };

        Assert.True(board.UpdateImage(image, newData, after));

        Assert.Equal(newData, image.Data);
        Assert.Equal(after, image.Metadata);
        Assert.Equal(5f, image.Left, 3);
        Assert.Equal(6f, image.Top, 3);
        Assert.Equal(20f, image.Width, 3);
        Assert.Equal(10f, image.Height, 3);
    }

    [Fact]
    public void UpdateImage_UndoRestoresPreviousContentAndRedoReappliesIt()
    {
        var board = new BoardService();
        var image = NewImage();
        image.Metadata = "old-meta";
        byte[] oldData = image.Data.ToArray();
        Assert.True(board.AddImage(image));

        byte[] newData = { 7, 7, 7 };
        Assert.True(board.UpdateImage(image, newData, "new-meta"));

        board.Current.Undo();
        Assert.Equal(oldData, image.Data);
        Assert.Equal("old-meta", image.Metadata);

        board.Current.Redo();
        Assert.Equal(newData, image.Data);
        Assert.Equal("new-meta", image.Metadata);
    }

    [Fact]
    public void UpdateImage_UnknownImage_ReturnsFalseAndChangesNothing()
    {
        var board = new BoardService();
        var present = NewImage();
        Assert.True(board.AddImage(present));

        var missing = NewImage(5);
        var originalData = present.Data.ToArray();
        Assert.False(board.UpdateImage(missing, new byte[] { 1 }, "meta"));

        Assert.Equal(originalData, present.Data);
        Assert.Equal("", present.Metadata);
        Assert.Single(board.CurrentImages);
    }

    [Fact]
    public void Serialize_RoundTripsBoardPlanMetadata()
    {
        var metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.BoardPlan,
            Schema = 2,
            Latex = "x^2 - 1",
            Text = "标题",
            Lines = new List<string> { "第一行", "第二行" },
            StyleJson = "{\"fontSize\":24,\"columns\":2}",
            Instruction = "整理当前页板书",
            CreatedAt = 1730000000000,
            Notes = new List<string> { "第 2 行置信度较低" },
        };

        string json = metadata.Serialize();
        var parsed = AiPasteMetadata.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(AiPasteKind.BoardPlan, parsed.Kind);
        Assert.Equal(2, parsed.Schema);
        Assert.Equal("x^2 - 1", parsed.Latex);
        Assert.Equal("标题", parsed.Text);
        Assert.Equal(2, parsed.Lines.Count);
        Assert.Equal("{\"fontSize\":24,\"columns\":2}", parsed.StyleJson);
        Assert.Equal("整理当前页板书", parsed.Instruction);
        Assert.Equal(1730000000000, parsed.CreatedAt);
        Assert.Single(parsed.Notes);
    }

    [Fact]
    public void TryParse_LegacyJson_DefaultsBoardPlanFields()
    {
        var parsed = AiPasteMetadata.TryParse("{\"kind\":1,\"text\":\"旧卡片\",\"lines\":[\"a\"]}");

        Assert.NotNull(parsed);
        Assert.Equal(AiPasteKind.Steps, parsed.Kind);
        Assert.Equal(1, parsed.Schema);
        Assert.Equal("", parsed.StyleJson);
        Assert.Equal("", parsed.Instruction);
        Assert.Equal(0, parsed.CreatedAt);
        Assert.Empty(parsed.Notes);
    }
}