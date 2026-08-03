using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using Xunit;

namespace CznTranslator.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Defaults_match_the_gpu_profile_from_the_spec()
    {
        var config = ConfigLoader.Parse("{}").Config;

        Assert.Equal(100, config.Detect.SettleMs);
        Assert.Equal(4, config.Detect.HashDistanceThreshold);
        Assert.Equal(1, config.Detect.PollEveryNthFrame);
        Assert.Equal(960, config.Ocr.Det.LimitSideLen);
        Assert.Equal(8, config.Ocr.Rec.BatchSize);
        Assert.False(config.Ocr.Quantized);
        Assert.Equal(0.85, config.Lookup.FuzzyThreshold);
    }

    [Fact]
    public void Laptop_profile_applies_every_override_from_the_table()
    {
        var config = ConfigLoader.Parse("""{ "profile": "laptop" }""").Config;

        Assert.True(config.Ocr.Quantized);
        Assert.Equal(640, config.Ocr.Det.LimitSideLen);
        Assert.Equal(4, config.Ocr.Rec.BatchSize);
        Assert.Equal(220, config.Detect.SettleMs);
        Assert.Equal(4, config.Detect.PollEveryNthFrame);
        Assert.Equal(0, config.Detect.AutoZoneMaxPriority);
    }

    [Fact]
    public void An_explicit_value_beats_the_profile_default()
    {
        // This is what makes the profile "a set of overrides, not a separate build" —
        // the user can still pin any individual knob.
        var config = ConfigLoader.Parse(
            """
            {
              "profile": "laptop",
              "detect": { "settleMs": 120 },
              "ocr": { "quantized": false }
            }
            """).Config;

        Assert.Equal(120, config.Detect.SettleMs);
        Assert.False(config.Ocr.Quantized);

        // Untouched keys still come from the profile.
        Assert.Equal(640, config.Ocr.Det.LimitSideLen);
        Assert.Equal(4, config.Detect.PollEveryNthFrame);
    }

    [Fact]
    public void An_unknown_profile_warns_instead_of_silently_doing_nothing()
    {
        var result = ConfigLoader.Parse("""{ "profile": "gaming-rig" }""");

        Assert.Contains(result.Warnings, w => w.Contains("gaming-rig", StringComparison.Ordinal));
        Assert.Equal(100, result.Config.Detect.SettleMs);
    }

    [Fact]
    public void Zones_are_parsed_with_modes_and_priorities()
    {
        var config = ConfigLoader.Parse(
            """
            {
              "zones": [
                { "id": "dialogue", "rect": [0.15, 0.72, 0.70, 0.20], "priority": 0, "mode": "block" },
                { "id": "tooltip",  "rect": [0.00, 0.00, 1.00, 1.00], "priority": 1, "mode": "auto" },
                { "id": "topbar",   "rect": [0.00, 0.00, 1.00, 0.08], "priority": 2, "mode": "line" }
              ]
            }
            """).Config;

        Assert.Equal(3, config.Zones.Length);
        Assert.Equal(ZoneMode.Block, config.Zones[0].Mode);
        Assert.Equal(ZoneMode.Line, config.Zones[2].Mode);
        Assert.Equal(new NormalizedRect(0.15, 0.72, 0.70, 0.20), config.Zones[0].NormalizedRect);
    }

    [Fact]
    public void A_zone_outside_the_unit_square_is_rejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Parse(
            """{ "zones": [ { "id": "bad", "rect": [0.5, 0.5, 0.8, 0.2] } ] }"""));

        Assert.Contains("bad", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_zone_ids_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ConfigLoader.Parse(
            """
            {
              "zones": [
                { "id": "dup", "rect": [0, 0, 1, 1] },
                { "id": "dup", "rect": [0, 0, 1, 0.5] }
              ]
            }
            """));
    }

    [Fact]
    public void An_unusable_capture_source_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ConfigLoader.Parse("""{ "capture": { "source": "obs" } }"""));
    }

    [Fact]
    public void Settle_outside_the_recommended_window_warns_but_loads()
    {
        var result = ConfigLoader.Parse("""{ "detect": { "settleMs": 20 } }""");

        Assert.Equal(20, result.Config.Detect.SettleMs);
        Assert.Contains(result.Warnings, w => w.Contains("settleMs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_laptop_settle_of_220ms_does_not_warn()
    {
        // §3 quotes a 60–200 ms range while §12 sets the laptop profile to 220 ms. The validator
        // has to accept the profile's own value without crying wolf on every laptop start-up.
        var result = ConfigLoader.Parse("""{ "profile": "laptop" }""");

        Assert.DoesNotContain(result.Warnings, w => w.Contains("settleMs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_multiple_of_32_side_length_warns()
    {
        var result = ConfigLoader.Parse("""{ "ocr": { "det": { "limitSideLen": 1000 } } }""");
        Assert.Contains(result.Warnings, w => w.Contains("limitSideLen", StringComparison.Ordinal));
    }

    [Fact]
    public void An_inverted_confidence_threshold_pair_warns()
    {
        var result = ConfigLoader.Parse(
            """{ "lookup": { "fuzzyThreshold": 0.80, "fuzzyThresholdLowConfidence": 0.90 } }""");

        Assert.Contains(result.Warnings, w => w.Contains("fuzzyThresholdLowConfidence", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_is_parsed_from_its_spelling_in_the_spec()
    {
        Assert.Equal(OcrProviderKind.Cpu, ConfigLoader.Parse("""{ "ocr": { "provider": "cpu" } }""").Config.Ocr.Provider);
        Assert.Equal(OcrProviderKind.Auto, ConfigLoader.Parse("""{ "ocr": { "provider": "auto" } }""").Config.Ocr.Provider);
    }

    [Fact]
    public void Round_trips_through_serialization()
    {
        var original = ConfigLoader.Parse("""{ "profile": "laptop", "detect": { "settleMs": 150 } }""").Config;
        var reloaded = ConfigLoader.Parse(ConfigLoader.Serialize(original)).Config;

        Assert.Equal(original.Detect.SettleMs, reloaded.Detect.SettleMs);
        Assert.Equal(original.Ocr.Det.LimitSideLen, reloaded.Ocr.Det.LimitSideLen);
        Assert.Equal(original.Profile, reloaded.Profile);
    }

    [Fact]
    public void Live_settings_flags_only_the_changes_that_need_a_restart()
    {
        var before = ConfigLoader.Parse("{}").Config;
        var after = ConfigLoader.Parse(
            """
            {
              "detect": { "settleMs": 150 },
              "overlay": { "fontSize": 22 },
              "ocr": { "provider": "cpu" }
            }
            """).Config;

        var restart = LiveSettings.Compare(before, after);

        Assert.Contains("ocr.provider", restart);
        Assert.DoesNotContain("detect.settleMs", restart);
        Assert.DoesNotContain("overlay.fontSize", restart);
    }
}

public class NormalizedRectTests
{
    [Fact]
    public void Projects_onto_a_1440p_surface()
    {
        var rect = new NormalizedRect(0.15, 0.72, 0.70, 0.20);
        var pixels = rect.ToPixels(2560, 1440);

        Assert.Equal(384, pixels.X);
        Assert.Equal(1037, pixels.Y);
        Assert.Equal(1792, pixels.Width);
        Assert.Equal(288, pixels.Height);
    }

    [Fact]
    public void The_same_zone_lands_proportionally_at_another_resolution()
    {
        var rect = new NormalizedRect(0.15, 0.72, 0.70, 0.20);

        var wide = rect.ToPixels(2560, 1440);
        var small = rect.ToPixels(1280, 720);

        Assert.Equal(wide.Width / 2, small.Width);
        Assert.Equal(wide.Height / 2, small.Height);
    }

    [Fact]
    public void Projection_never_leaves_the_surface()
    {
        var pixels = NormalizedRect.Full.ToPixels(1920, 1080);

        Assert.Equal(0, pixels.X);
        Assert.Equal(0, pixels.Y);
        Assert.Equal(1920, pixels.Width);
        Assert.Equal(1080, pixels.Height);
    }

    [Fact]
    public void Validity_matches_the_unit_square()
    {
        Assert.True(new NormalizedRect(0, 0, 1, 1).IsValid);
        Assert.False(new NormalizedRect(0.5, 0, 0.8, 1).IsValid);
        Assert.False(new NormalizedRect(0, 0, 0, 1).IsValid);
        Assert.False(new NormalizedRect(-0.1, 0, 0.5, 0.5).IsValid);
    }
}
