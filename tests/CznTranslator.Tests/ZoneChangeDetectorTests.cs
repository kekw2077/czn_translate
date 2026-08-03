using CznTranslator.Core.Config;
using CznTranslator.Detect;
using Xunit;

namespace CznTranslator.Tests;

public class ZoneChangeDetectorTests
{
    private const ulong HashA = 0x0F0F_0F0F_0F0F_0F0FUL;
    private const ulong HashB = 0xF0F0_F0F0_F0F0_F0F0UL;

    [Fact]
    public void First_observation_is_dirty_so_the_zone_gets_recognized()
    {
        var detector = new ZoneChangeDetector(settleMs: 100);
        Assert.Equal(ZoneState.Dirty, detector.Observe("dialogue", HashA, 0).State);
    }

    [Fact]
    public void Settles_only_after_the_full_window()
    {
        var detector = new ZoneChangeDetector(settleMs: 100);

        detector.Observe("dialogue", HashA, 0);
        Assert.Equal(ZoneState.Dirty, detector.Observe("dialogue", HashA, 99).State);
        Assert.Equal(ZoneState.Settled, detector.Observe("dialogue", HashA, 100).State);
    }

    [Fact]
    public void Settles_exactly_once_per_stable_screen()
    {
        var detector = new ZoneChangeDetector(settleMs: 100);

        detector.Observe("dialogue", HashA, 0);
        Assert.Equal(ZoneState.Settled, detector.Observe("dialogue", HashA, 150).State);

        // Re-firing here would re-run OCR on an unchanged screen every single frame.
        Assert.Equal(ZoneState.Idle, detector.Observe("dialogue", HashA, 200).State);
        Assert.Equal(ZoneState.Idle, detector.Observe("dialogue", HashA, 5000).State);
    }

    [Fact]
    public void A_change_restarts_the_window()
    {
        var detector = new ZoneChangeDetector(settleMs: 100);

        detector.Observe("dialogue", HashA, 0);
        detector.Observe("dialogue", HashA, 150);

        Assert.Equal(ZoneState.Dirty, detector.Observe("dialogue", HashB, 200).State);
        Assert.Equal(ZoneState.Dirty, detector.Observe("dialogue", HashB, 299).State);
        Assert.Equal(ZoneState.Settled, detector.Observe("dialogue", HashB, 300).State);
    }

    [Fact]
    public void Noise_below_the_threshold_does_not_restart_the_window()
    {
        var detector = new ZoneChangeDetector(settleMs: 100, hashDistanceThreshold: 4);
        detector.Observe("dialogue", HashA, 0);

        var noisy = HashA ^ 0b1011UL; // 3 bits — under the threshold
        Assert.Equal(ZoneState.Dirty, detector.Observe("dialogue", noisy, 50).State);
        Assert.Equal(ZoneState.Settled, detector.Observe("dialogue", noisy, 100).State);
    }

    [Fact]
    public void Reference_hash_is_not_refreshed_by_sub_threshold_drift()
    {
        // A fade-in moves a couple of bits per frame. If the reference tracked every frame the
        // zone would drift arbitrarily far while always reading as "unchanged" — the §11 trap.
        var detector = new ZoneChangeDetector(settleMs: 50, hashDistanceThreshold: 4);
        detector.Observe("fade", 0UL, 0);

        Assert.Equal(ZoneState.Dirty, detector.Observe("fade", 0b11UL, 10).State);
        Assert.Equal(ZoneState.Dirty, detector.Observe("fade", 0b1111UL, 20).State);

        // Cumulative distance from the original reference is now 6 bits — a real change.
        Assert.Equal(ZoneState.Dirty, detector.Observe("fade", 0b111111UL, 30).State);
        Assert.Equal(ZoneState.Settled, detector.Observe("fade", 0b111111UL, 90).State);
    }

    [Fact]
    public void Zones_are_tracked_independently()
    {
        var detector = new ZoneChangeDetector(settleMs: 100);

        detector.Observe("dialogue", HashA, 0);
        detector.Observe("topbar", HashB, 0);
        detector.Observe("dialogue", HashA, 100);

        Assert.Equal(ZoneState.Idle, detector.Observe("dialogue", HashA, 110).State);
        Assert.Equal(ZoneState.Settled, detector.Observe("topbar", HashB, 110).State);
    }

    [Fact]
    public void Invalidate_forces_the_next_observation_to_be_a_change()
    {
        var detector = new ZoneChangeDetector(settleMs: 100);
        detector.Observe("dialogue", HashA, 0);
        detector.Observe("dialogue", HashA, 100);

        detector.Invalidate("dialogue");

        Assert.Equal(ZoneState.Dirty, detector.Observe("dialogue", HashA, 110).State);
        Assert.Equal(ZoneState.Settled, detector.Observe("dialogue", HashA, 210).State);
    }

    [Fact]
    public void Laptop_profile_demotes_background_auto_zones_to_manual()
    {
        var detect = new DetectSection { AutoZoneMaxPriority = 0 };

        var dialogue = new ZoneConfig { Id = "dialogue", Priority = 0, Mode = ZoneMode.Auto };
        var tooltip = new ZoneConfig { Id = "tooltip", Priority = 1, Mode = ZoneMode.Auto };
        var topbar = new ZoneConfig { Id = "topbar", Priority = 2, Mode = ZoneMode.Line };

        Assert.True(ZoneChangeDetector.IsPolledAutomatically(dialogue, detect));
        Assert.False(ZoneChangeDetector.IsPolledAutomatically(tooltip, detect));

        // Only 'auto' zones are demoted — an explicit 'line' zone keeps its mode.
        Assert.Equal(ZoneMode.Line, ZoneChangeDetector.EffectiveMode(topbar, detect));
    }

    [Fact]
    public void Default_profile_polls_every_auto_zone()
    {
        var detect = new DetectSection();
        var tooltip = new ZoneConfig { Id = "tooltip", Priority = 7, Mode = ZoneMode.Auto };

        Assert.True(ZoneChangeDetector.IsPolledAutomatically(tooltip, detect));
    }

    [Fact]
    public void Manual_zones_are_never_polled()
    {
        var scrolling = new ZoneConfig { Id = "inventory", Priority = 0, Mode = ZoneMode.Manual };
        Assert.False(ZoneChangeDetector.IsPolledAutomatically(scrolling, new DetectSection()));
    }
}
