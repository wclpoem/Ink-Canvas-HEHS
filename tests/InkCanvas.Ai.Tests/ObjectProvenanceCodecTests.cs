using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>对象溯源 JSON 编解码：往返保真、空值与畸形载荷安全失败。</summary>
public sealed class ObjectProvenanceCodecTests
{
    [Fact]
    public void EncodeAndTryDecode_RoundTripsAllFields()
    {
        var original = new ObjectProvenance
        {
            SessionId = "session-1",
            RequestId = "req-1",
            BoardId = "main",
            PageId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SlideId = 7,
            Source = ObjectSource.AiWrite,
            Instruction = "把这一页整理成分步板书",
            CreatedAtMs = 1730000000000,
            GroupId = "group-1",
        };

        string json = ObjectProvenanceCodec.Encode(original);

        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.True(ObjectProvenanceCodec.TryDecode(json, out ObjectProvenance decoded));
        Assert.NotNull(decoded);
        Assert.Equal(original.SessionId, decoded.SessionId);
        Assert.Equal(original.RequestId, decoded.RequestId);
        Assert.Equal(original.BoardId, decoded.BoardId);
        Assert.Equal(original.PageId, decoded.PageId);
        Assert.Equal(original.SlideId, decoded.SlideId);
        Assert.Equal(original.Source, decoded.Source);
        Assert.Equal(original.Instruction, decoded.Instruction);
        Assert.Equal(original.CreatedAtMs, decoded.CreatedAtMs);
        Assert.Equal(original.GroupId, decoded.GroupId);
    }

    [Fact]
    public void Encode_Null_ReturnsNull()
    {
        Assert.Null(ObjectProvenanceCodec.Encode(null));
        Assert.Null(ObjectProvenanceCodec.ToJson(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{\"sessionId\":")]
    [InlineData("{\"source\":\"notARealSource\"}")]
    public void TryDecode_MalformedOrEmptyJson_ReturnsFalse(string payload)
    {
        Assert.False(ObjectProvenanceCodec.TryDecode(payload, out ObjectProvenance decoded));
        Assert.Null(decoded);
    }
}