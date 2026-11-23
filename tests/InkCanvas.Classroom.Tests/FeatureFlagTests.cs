using System.Text.Json;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>功能开关回归：默认全开、读写兼容、旧设置文件升级与 FeatureGate 判定。</summary>
public sealed class FeatureFlagTests
{
    [Fact]
    public void DefaultFeatures_AreAllEnabled()
    {
        var features = new FeatureSettings();

        Assert.True(features.EnableAiTeaching);
        Assert.True(features.EnableExerciseFlow);
        Assert.True(features.EnableExampleLibrary);
        Assert.True(features.EnableResourceBundle);
        Assert.True(features.UnifiedContext);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesIndividualSwitches()
    {
        var settings = new AppSettings();
        settings.Features.EnableExerciseFlow = false;
        settings.Features.EnableResourceBundle = false;

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.Features.EnableExerciseFlow);
        Assert.False(restored.Features.EnableResourceBundle);
        Assert.True(restored.Features.EnableAiTeaching);
        Assert.True(restored.Features.EnableExampleLibrary);
    }

    [Fact]
    public void LegacyJsonWithoutFeaturesKey_FallsBackToAllEnabled()
    {
        const string legacyJson = """
            {
              "advanced": { "isLogEnabled": true },
              "canvas": { "inkWidth": 3.0 }
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.Features);
        Assert.True(settings.Features.EnableAiTeaching);
        Assert.True(settings.Features.EnableExerciseFlow);
        Assert.True(settings.Features.EnableExampleLibrary);
        Assert.True(settings.Features.EnableResourceBundle);
        Assert.True(settings.Features.UnifiedContext);
    }

    [Fact]
    public void LegacyFeaturesNodeWithoutUnifiedContextKey_DefaultsToEnabled()
    {
        const string legacyJson = """
            {
              "features": { "enableAiTeaching": false, "enableExerciseFlow": false }
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson);

        Assert.NotNull(settings);
        Assert.False(settings!.Features.EnableAiTeaching);
        Assert.False(settings.Features.EnableExerciseFlow);
        Assert.True(settings.Features.UnifiedContext);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesDisabledUnifiedContext()
    {
        var settings = new AppSettings();
        settings.Features.UnifiedContext = false;

        var json = JsonSerializer.Serialize(settings);

        Assert.Contains("unifiedContext", json);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.False(restored!.Features.UnifiedContext);
    }

    [Fact]
    public void FeatureGate_ReturnsFalseForDisabledSwitch()
    {
        var settings = new AppSettings();
        settings.Features.EnableExerciseFlow = false;

        Assert.False(FeatureGate.IsEnabled(FeatureKind.ExerciseFlow, settings));
        Assert.True(FeatureGate.IsEnabled(FeatureKind.AiTeaching, settings));
        Assert.True(FeatureGate.IsEnabled(FeatureKind.ExampleLibrary, settings));
        Assert.True(FeatureGate.IsEnabled(FeatureKind.ResourceBundle, settings));
    }

    [Fact]
    public void FeatureGate_NullSettingsOrFeatures_ReturnsTrue()
    {
        Assert.True(FeatureGate.IsEnabled(FeatureKind.AiTeaching, null));

        var settings = new AppSettings { Features = null! };
        Assert.True(FeatureGate.IsEnabled(FeatureKind.ExerciseFlow, settings));
    }

    [Fact]
    public void FeatureGate_UnknownKind_ReturnsTrue()
    {
        var settings = new AppSettings();
        settings.Features.EnableAiTeaching = false;

        Assert.True(FeatureGate.IsEnabled((FeatureKind)999, settings));
    }
}