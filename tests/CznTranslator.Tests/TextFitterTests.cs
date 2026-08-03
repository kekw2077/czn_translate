using CznTranslator.Core.Config;
using CznTranslator.Core.Overlay;
using Xunit;

namespace CznTranslator.Tests;

public class TextFitterTests
{
    /// <summary>
    /// Monospace stand-in for DirectWrite: every glyph is 0.5×fontSize wide, every line is
    /// 1.2×fontSize tall, and wrapping breaks on spaces. Proportional enough to exercise the
    /// ladder without dragging a font stack into the tests.
    /// </summary>
    private sealed class FakeMeasurer(double advanceRatio = 0.5, double lineRatio = 1.2) : ITextMeasurer
    {
        public int Calls { get; private set; }

        public TextExtent Measure(string text, double fontSize, double maxWidth)
        {
            Calls++;

            var advance = fontSize * advanceRatio;
            var lineHeight = fontSize * lineRatio;

            if (maxWidth <= 0)
                return new TextExtent(text.Length * advance, lineHeight);

            var perLine = Math.Max(1, (int)(maxWidth / advance));
            var lines = 1;
            var used = 0;
            var longest = 0;

            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var needed = used == 0 ? word.Length : word.Length + 1;
                if (used + needed > perLine && used > 0)
                {
                    longest = Math.Max(longest, used);
                    lines++;
                    used = word.Length;
                }
                else
                {
                    used += needed;
                }
            }

            longest = Math.Max(longest, used);

            // A word longer than the wrap width overflows rather than being cut, which is what
            // DirectWrite does without an emergency break — so the measured width can exceed
            // maxWidth, and the fitter has to cope with that.
            return new TextExtent(longest * advance, lines * lineHeight);
        }
    }

    private static OverlaySection Settings(double fontSize = 20, double minScale = 0.8) =>
        new() { FontSize = fontSize, MinFontScale = minScale };

    [Fact]
    public void Text_that_already_fits_is_left_alone()
    {
        var fit = TextFitter.Fit("Атака", boxWidth: 400, boxHeight: 60, Settings(), new FakeMeasurer());

        Assert.Equal(FitOutcome.AsIs, fit.Outcome);
        Assert.Equal(20, fit.FontSize);
        Assert.False(fit.Wrap);
        Assert.Equal("Атака", fit.Text);
    }

    [Fact]
    public void Slightly_oversized_text_shrinks_before_wrapping()
    {
        // 22 chars × 10 px = 220 px at size 20; the box takes 200, so 18 pt lands it.
        var fit = TextFitter.Fit(new string('x', 22), boxWidth: 200, boxHeight: 60, Settings(), new FakeMeasurer());

        Assert.Equal(FitOutcome.Shrunk, fit.Outcome);
        Assert.True(fit.FontSize < 20);
        Assert.True(fit.FontSize >= 16);
        Assert.False(fit.Wrap);
    }

    [Fact]
    public void Shrinking_never_goes_below_the_configured_floor()
    {
        // Illegible text is not a fit. Past the floor the ladder moves to wrapping instead.
        var fit = TextFitter.Fit(
            "Наносит двенадцать единиц урона случайному врагу и накладывает ожог",
            boxWidth: 200,
            boxHeight: 200,
            Settings(),
            new FakeMeasurer());

        Assert.True(fit.FontSize >= 20 * 0.8 - 1e-9);
    }

    [Fact]
    public void Text_too_wide_to_shrink_gets_wrapped()
    {
        var fit = TextFitter.Fit(
            "Наносит двенадцать единиц урона случайному врагу",
            boxWidth: 200,
            boxHeight: 200,
            Settings(),
            new FakeMeasurer());

        Assert.Equal(FitOutcome.Wrapped, fit.Outcome);
        Assert.True(fit.Wrap);
        Assert.False(fit.NeedsHoverToRead);
        Assert.Equal("Наносит двенадцать единиц урона случайному врагу", fit.Text);
    }

    [Fact]
    public void A_short_box_truncates_with_an_ellipsis()
    {
        var fit = TextFitter.Fit(
            "Наносит двенадцать единиц урона случайному врагу и накладывает ожог на три хода",
            boxWidth: 120,
            boxHeight: 24,
            Settings(),
            new FakeMeasurer());

        Assert.Equal(FitOutcome.Truncated, fit.Outcome);
        Assert.EndsWith(TextFitter.Ellipsis, fit.Text, StringComparison.Ordinal);
        Assert.True(fit.NeedsHoverToRead);
    }

    [Fact]
    public void Truncation_cuts_on_a_word_boundary()
    {
        var fit = TextFitter.Fit(
            "Наносит двенадцать единиц урона случайному врагу и накладывает ожог",
            boxWidth: 160,
            boxHeight: 24,
            Settings(),
            new FakeMeasurer());

        var body = fit.Text[..^TextFitter.Ellipsis.Length];
        Assert.DoesNotContain("  ", body, StringComparison.Ordinal);

        // Whatever survived is whole words from the start of the translation.
        Assert.StartsWith(body.Split(' ')[0], "Наносит", StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_unbreakable_word_falls_back_to_a_character_cut()
    {
        // Word-boundary truncation has nothing to work with here; something still has to render.
        var fit = TextFitter.Fit(
            new string('ю', 200),
            boxWidth: 60,
            boxHeight: 20,
            Settings(),
            new FakeMeasurer());

        Assert.Equal(FitOutcome.Truncated, fit.Outcome);
        Assert.EndsWith(TextFitter.Ellipsis, fit.Text, StringComparison.Ordinal);
        Assert.True(fit.Text.Length < 200);
    }

    [Fact]
    public void A_minFontScale_of_one_disables_shrinking_entirely()
    {
        var fit = TextFitter.Fit(
            "Наносит двенадцать единиц урона",
            boxWidth: 200,
            boxHeight: 200,
            Settings(minScale: 1.0),
            new FakeMeasurer());

        Assert.Equal(20, fit.FontSize);
        Assert.Equal(FitOutcome.Wrapped, fit.Outcome);
    }

    [Fact]
    public void An_empty_box_is_rejected_rather_than_looping()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TextFitter.Fit("Атака", 0, 40, Settings(), new FakeMeasurer()));
    }

    [Fact]
    public void Fitting_stays_within_a_bounded_number_of_measurements()
    {
        // This runs per line per redraw; an unbounded search would show up as overlay stutter.
        var measurer = new FakeMeasurer();
        TextFitter.Fit(
            "Наносит двенадцать единиц урона случайному врагу и накладывает ожог на три хода",
            boxWidth: 120,
            boxHeight: 24,
            Settings(),
            measurer);

        Assert.True(measurer.Calls < 120, $"Took {measurer.Calls} measurements.");
    }
}
