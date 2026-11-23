using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class ObjectProvenanceTests
{
    private static readonly Guid PageId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ObjectProvenance Sample() => new()
    {
        SessionId = "session-1",
        RequestId = "req-1",
        BoardId = "main",
        PageId = PageId,
        SlideId = 7,
        Source = ObjectSource.AiWrite,
        Instruction = "把左侧板书整理成三步",
        CreatedAtMs = 1730000000000,
        GroupId = "group-1",
    };

    [Fact]
    public void ToJson_RoundTripsAllFields()
    {
        ObjectProvenance original = Sample();

        string json = original.ToJson();

        Assert.Contains("\"sessionId\"", json);
        Assert.Contains("\"requestId\"", json);
        Assert.Contains("\"boardId\"", json);
        Assert.Contains("\"pageId\"", json);
        Assert.Contains("\"slideId\"", json);
        Assert.Contains("\"source\"", json);
        Assert.Contains("\"instruction\"", json);
        Assert.Contains("\"createdAtMs\"", json);
        Assert.Contains("\"groupId\"", json);

        Assert.True(ObjectProvenance.TryParse(json, out ObjectProvenance parsed));
        Assert.NotNull(parsed);
        Assert.Equal("session-1", parsed.SessionId);
        Assert.Equal("req-1", parsed.RequestId);
        Assert.Equal("main", parsed.BoardId);
        Assert.Equal(PageId, parsed.PageId);
        Assert.Equal(7, parsed.SlideId);
        Assert.Equal(ObjectSource.AiWrite, parsed.Source);
        Assert.Equal("把左侧板书整理成三步", parsed.Instruction);
        Assert.Equal(1730000000000, parsed.CreatedAtMs);
        Assert.Equal("group-1", parsed.GroupId);
    }

    [Fact]
    public void ToJson_SerializesSourceAsReadableName()
    {
        var provenance = Sample() with { Source = ObjectSource.TeacherEdit };

        string json = provenance.ToJson();

        Assert.Contains("\"TeacherEdit\"", json);
        Assert.True(ObjectProvenance.TryParse(json, out ObjectProvenance parsed));
        Assert.Equal(ObjectSource.TeacherEdit, parsed.Source);
    }

    [Fact]
    public void OptionalSlideAndPage_AllowEmptyValues()
    {
        var provenance = new ObjectProvenance
        {
            SessionId = "s",
            RequestId = "r",
            BoardId = "side",
            PageId = Guid.Empty,
            SlideId = null,
            Source = ObjectSource.UserInk,
        };

        Assert.True(ObjectProvenance.TryParse(provenance.ToJson(), out ObjectProvenance parsed));
        Assert.Equal(Guid.Empty, parsed.PageId);
        Assert.Null(parsed.SlideId);
        Assert.Equal(ObjectSource.UserInk, parsed.Source);
    }

    [Fact]
    public void ToJson_ClampsLongInstructionAndIdentifiers()
    {
        var provenance = new ObjectProvenance
        {
            SessionId = new string('s', ObjectProvenance.MaxIdLength + 40),
            RequestId = new string('r', ObjectProvenance.MaxIdLength + 40),
            BoardId = new string('b', ObjectProvenance.MaxBoardIdLength + 40),
            Instruction = new string('指', ObjectProvenance.MaxInstructionLength + 40),
            GroupId = new string('g', ObjectProvenance.MaxGroupIdLength + 40),
            Source = ObjectSource.AiOrganize,
        };

        Assert.True(ObjectProvenance.TryParse(provenance.ToJson(), out ObjectProvenance parsed));
        Assert.Equal(ObjectProvenance.MaxIdLength, parsed.SessionId.Length);
        Assert.Equal(ObjectProvenance.MaxIdLength, parsed.RequestId.Length);
        Assert.Equal(ObjectProvenance.MaxBoardIdLength, parsed.BoardId.Length);
        Assert.Equal(ObjectProvenance.MaxInstructionLength, parsed.Instruction.Length);
        Assert.Equal(ObjectProvenance.MaxGroupIdLength, parsed.GroupId.Length);
    }

    [Fact]
    public void MalformedJson_ReturnsFalseWithoutThrowing()
    {
        Assert.False(ObjectProvenance.TryParse("{not-json", out ObjectProvenance parsed));
        Assert.Null(parsed);
        Assert.False(ObjectProvenance.TryParse("", out _));
        Assert.False(ObjectProvenance.TryParse(null, out _));
        Assert.Null(ObjectProvenanceCodec.ToJson(null));
    }

    [Fact]
    public void UnknownOrNumericSource_ReturnsFalseInsteadOfThrowing()
    {
        const string unknownSource = "{\"source\":\"bogus\"}";
        const string numericSource = "{\"source\":3}";

        Assert.False(ObjectProvenance.TryParse(unknownSource, out _));
        Assert.False(ObjectProvenance.TryParse(numericSource, out _));
    }

    [Fact]
    public void MissingFields_FallBackToDefaults()
    {
        const string minimal = "{\"boardId\":\"main\"}";

        Assert.True(ObjectProvenance.TryParse(minimal, out ObjectProvenance parsed));
        Assert.Equal("main", parsed.BoardId);
        Assert.Equal("", parsed.SessionId);
        Assert.Equal("", parsed.RequestId);
        Assert.Equal(Guid.Empty, parsed.PageId);
        Assert.Null(parsed.SlideId);
        Assert.Equal(ObjectSource.Unknown, parsed.Source);
        Assert.Equal(0, parsed.CreatedAtMs);
    }
}