using System.Text.Json;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class AppearanceSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appearance\":{}}")]
    public void NewAndOlderSettingsDefaultToRegularIcons(string json)
        => Assert.True(JsonSerializer.Deserialize<AppSettings>(json).Appearance.IsIconRegularStyle);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitIconStyleSurvivesSettingsRoundtrip(bool regular)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"appearance\":{\"isIconRegularStyle\":" + regular.ToString().ToLowerInvariant() + "}}");
        Assert.Equal(regular, settings.Appearance.IsIconRegularStyle);
        Assert.Equal(regular, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)).Appearance.IsIconRegularStyle);
    }
}
