using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PptSlidePreviewSourceTests
{
    private static PptPresentationInfo Deck(int count) => new("deck-" + count, "Large", "Large.pptx", true,
        count, 960, 540, "PowerPoint.Application");

    [Theory]
    [InlineData(201)]
    [InlineData(801)]
    public async Task OnlyRequestedPagesRenderAndCacheStaysBounded(int count)
    {
        using var transport = new Transport();
        using var previews = new PptSlidePreviewSource(transport);
        var presentation = Deck(count);
        Assert.Empty(transport.Calls);
        await previews.GetPageAsync(presentation, count, default);
        await previews.GetPageAsync(presentation, count, default);
        Assert.Equal(new[] { count }, transport.Calls);
        for (int index = 1; index <= 100; index++) await previews.GetPageAsync(presentation, index, default);
        Assert.InRange(previews.CachedCount, 1, 32);
        Assert.InRange(previews.CachedBytes, 1, 16 * 1024 * 1024);
    }

    [Fact]
    public async Task ChangingSlideCountInvalidatesIndexBasedPreviews()
    {
        using var transport = new Transport();
        using var previews = new PptSlidePreviewSource(transport);
        var presentation = Deck(801);
        await previews.GetPageAsync(presentation, 2, default);
        await previews.GetPageAsync(presentation with { SlideCount = 802 }, 2, default);
        Assert.Equal(new[] { 2, 2 }, transport.Calls);
    }

    [Fact]
    public async Task CompressedPreviewCacheAlsoHonorsByteLimit()
    {
        using var transport = new Transport { Render = (index, _) => Task.FromResult(Page(index) with { Image = new byte[2 * 1024 * 1024] }) };
        using var previews = new PptSlidePreviewSource(transport);
        for (int index = 1; index <= 20; index++) await previews.GetPageAsync(Deck(801), index, default);
        Assert.Equal(8, previews.CachedCount);
        Assert.Equal(16 * 1024 * 1024, previews.CachedBytes);
    }

    [Fact]
    public async Task HungPageTimesOutOnceAndFollowingPagesRemainAvailable()
    {
        using var transport = new Transport { Render = (index, _) => index == 1
            ? new TaskCompletionSource<PptSlidePreviewPage>().Task : Task.FromResult(Page(index)) };
        using var previews = new PptSlidePreviewSource(transport, TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAsync<TimeoutException>(() => previews.GetPageAsync(Deck(801), 1, default));
        await Assert.ThrowsAsync<TimeoutException>(() => previews.GetPageAsync(Deck(801), 1, default));
        var next = await previews.GetPageAsync(Deck(801), 801, default);
        Assert.Equal(801, next.Slide.Index);
        Assert.Equal(new[] { 1, 801 }, transport.Calls);
        Assert.Equal(1, previews.TimeoutCount);
    }

    [Fact]
    public async Task CancellationDropsOldViewportWithoutDisablingThatPage()
    {
        using var transport = new Transport { Render = async (index, token) =>
        {
            await Task.Delay(500, token);
            return Page(index);
        } };
        using var previews = new PptSlidePreviewSource(transport);
        using var canceled = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previews.GetPageAsync(Deck(801), 400, canceled.Token));
        transport.Render = (index, _) => Task.FromResult(Page(index));
        Assert.Equal(400, (await previews.GetPageAsync(Deck(801), 400, default)).Slide.Index);
        Assert.Equal(0, previews.TimeoutCount);
    }

    [Fact]
    public async Task BrokenPreviewIsSkippedAndOtherPresentationDoesNotReuseIt()
    {
        using var transport = new Transport { Render = (_, _) => throw new IOException("broken slide") };
        using var previews = new PptSlidePreviewSource(transport);
        await Assert.ThrowsAsync<IOException>(() => previews.GetPageAsync(Deck(201), 3, default));
        transport.Render = (index, _) => Task.FromResult(Page(index));
        Assert.Equal(3, (await previews.GetPageAsync(Deck(801), 3, default)).Slide.Index);
    }

    private static PptSlidePreviewPage Page(int index) => new(new(index + 255, index, "", null, null, null), new byte[1024]);

    private sealed class Transport : IPptSlidePreviewTransport
    {
        public List<int> Calls { get; } = new();
        public Func<int, CancellationToken, Task<PptSlidePreviewPage>> Render { get; set; } = (index, _) => Task.FromResult(Page(index));
        public Task<PptSlidePreviewPage> RenderAsync(PptPresentationInfo presentation, int index, CancellationToken token)
        {
            Calls.Add(index);
            return Render(index, token);
        }
        public void Dispose() { }
    }
}
