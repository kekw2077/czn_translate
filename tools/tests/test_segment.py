"""Masking, restoring and the plain-text round trip.

The property that matters most is reassembly being lossless: mask a string, "translate" each
segment to itself, rebuild, and get the original back byte for byte. Every other guarantee here
rests on that one.
"""

import json

import pytest

import apply_text
import extract_text
from czn.segment import (
    MARKER,
    collect_keywords,
    mask_segment,
    mask_string,
    rebuild,
    restore_markers,
)

SAMPLES = [
    "Blood Pact",
    "<#F9385D>Deal $Fixed Damage$ to all</>",
    "#basic_ev_0# $Morale$<br>Move <cc>1</> card to the $Discard Pile$",
    "Deal {0} damage over {1} turns",
    'Restores %s health<br><br>[shake]<#FFC978>my design</>[/shake]',
    "   <#FFFBC9> \"Authorization confirmed.\"",
    "<$shake>--static---@!$3!</$shake>",
    "#basic_ev_0# $Charm$",
    "12345",
]


class TestMasking:
    def test_markers_become_numbered_sentinels(self):
        segment = mask_segment("<#F9385D>Deal $Fixed Damage$ to all</>")

        assert segment.masked == "[0]Deal [1] to all[2]"
        assert segment.markers == ("<#F9385D>", "$Fixed Damage$", "</>")

    def test_angle_tags_swallow_their_own_dollar(self):
        """<$shake> must be consumed whole. If the '$' inside it opened a keyword match, it
        would run to the next '$' several words later and eat a sentence."""
        segment = mask_segment("<$shake>--static---@!$3!</$shake>")

        assert all("shake" in m for m in segment.markers if m.startswith("<"))
        assert "static" in segment.masked

    def test_a_keyword_may_contain_spaces(self):
        # $Discard Pile$ is the case the original strict pattern missed, which is how those
        # strings reached a translator that turned '$' into "долларов США".
        assert mask_segment("to the $Discard Pile$").markers == ("$Discard Pile$",)

    def test_a_keyword_never_crosses_a_tag(self):
        segment = mask_segment("$Resolve<br>When used, <cc>1</> $")
        assert not any(m.startswith("$") and "<" in m for m in segment.markers)

    def test_line_breaks_split_segments(self):
        masked = mask_string("First line<br>Second line")

        assert len(masked.segments) == 2
        assert masked.separators == ("<br>",)
        assert masked.segments[1].masked == "Second line"

    def test_the_original_break_spelling_is_kept(self):
        assert mask_string("a<br/>b").separators == ("<br/>",)
        assert mask_string("a<BR>b").separators == ("<BR>",)

    @pytest.mark.parametrize("text", ["#basic_ev_0# $Charm$", "12345", "{0}/{1}", "   "])
    def test_segments_with_no_words_are_not_translatable(self, text):
        assert not mask_segment(text).translatable

    @pytest.mark.parametrize("text", ["Blood Pact", "[0] damage", "Deal {0} damage"])
    def test_segments_with_words_are_translatable(self, text):
        assert mask_segment(text).translatable


class TestRestoring:
    def test_markers_come_back(self):
        segment = mask_segment("<#F00>Deal $Shield$</>")
        text, missing = restore_markers(segment, "[0]Наносит [1][2]")

        assert text == "<#F00>Наносит $Shield$</>"
        assert missing == []

    def test_reordered_sentinels_are_fine(self):
        """Russian word order moves things around; only presence is required, not position."""
        segment = mask_segment("Deal {0} damage to {1}")
        text, missing = restore_markers(segment, "[1] получает [0] урона")

        assert text == "{1} получает {0} урона"
        assert missing == []

    def test_spaces_inside_a_sentinel_are_tolerated(self):
        # Machine translators pad brackets; refusing those would fail most of a real file.
        segment = mask_segment("<b>Attack</b>")
        text, missing = restore_markers(segment, "[ 0 ]Атака[ 1 ]")

        assert text == "<b>Атака</b>"
        assert missing == []

    def test_a_lost_sentinel_is_reported(self):
        segment = mask_segment("<#F00>Attack</>")
        _text, missing = restore_markers(segment, "Атака[1]")

        assert missing == [0]

    def test_the_glossary_translates_keyword_contents(self):
        segment = mask_segment("Move to the $Discard Pile$")
        text, _ = restore_markers(segment, "Переместить в [0]", {"Discard Pile": "Сброс"})

        assert text == "Переместить в $Сброс$"

    def test_an_unknown_term_keeps_its_english(self):
        segment = mask_segment("Gain $Unmapped Thing$")
        text, _ = restore_markers(segment, "Получить [0]", {"Shield": "Щит"})

        assert text == "Получить $Unmapped Thing$"

    def test_a_stray_sentinel_index_is_left_alone(self):
        segment = mask_segment("<b>Attack</b>")
        text, _ = restore_markers(segment, "[0]Атака[1] [9]")

        assert text.endswith("[9]")


class TestRebuild:
    @pytest.mark.parametrize("source", SAMPLES)
    def test_identity_translation_reproduces_the_source(self, source):
        masked = mask_string(source)
        identity = {segment.masked: segment.masked for segment in masked.segments}

        text, problems = rebuild(masked, identity)

        assert text == source
        assert problems == []

    def test_an_untranslated_segment_stays_english(self):
        masked = mask_string("Translated part<br>Untouched part")
        text, problems = rebuild(masked, {"Translated part": "Переведено"})

        assert text == "Переведено<br>Untouched part"
        assert problems == []

    def test_a_segment_that_lost_a_marker_falls_back_to_english(self):
        """Dropping a </> would leave the colour span open and repaint the rest of the screen —
        far worse than one line staying in English."""
        masked = mask_string("<#F00>Attack</>")
        text, problems = rebuild(masked, {"[0]Attack[1]": "Атака"})

        assert text == "<#F00>Attack</>"
        assert len(problems) == 1
        assert "</>" in problems[0]

    def test_the_glossary_applies_even_to_untranslated_strings(self):
        # The term is decided once and holds everywhere, whether or not the sentence around it
        # made it through.
        masked = mask_string("#ev_0# $Shield$")
        text, _ = rebuild(masked, {}, {"Shield": "Щит"})

        assert text == "#ev_0# $Щит$"

    def test_empty_source(self):
        text, problems = rebuild(mask_string(""), {})
        assert (text, problems) == ("", [])


class TestGlossaryCollection:
    def test_terms_are_deduplicated_and_sorted(self):
        terms = collect_keywords(["$Shield$ and $Draw$", "$Shield$ again"])
        assert terms == ["Draw", "Shield"]

    def test_tag_internals_are_not_terms(self):
        assert collect_keywords(["<$shake>text</$shake>"]) == []


class TestFileRoundTrip:
    """extract -> translate -> apply, over the real CLIs."""

    @pytest.fixture
    def workspace(self, tmp_path):
        source = tmp_path / "all_en.json"
        source.write_text(json.dumps(SAMPLES, ensure_ascii=False), encoding="utf-8")
        return tmp_path, source

    def _translate(self, out_dir, transform, glossary=None):
        manifest = json.loads((out_dir / "manifest.json").read_text(encoding="utf-8"))
        for part in manifest["parts"]:
            lines = [transform(line) for line in part["segments"]]
            (out_dir / part["file"].replace(".txt", ".ru.txt")).write_text(
                "\n".join(lines) + "\n", encoding="utf-8"
            )

        terms = (out_dir / "glossary_terms.txt").read_text(encoding="utf-8").split("\n")[:-1]
        mapping = glossary or {}
        (out_dir / "glossary_terms.ru.txt").write_text(
            "\n".join(mapping.get(t, t) for t in terms) + "\n", encoding="utf-8"
        )

    def test_extract_writes_parts_and_a_glossary(self, workspace):
        tmp_path, source = workspace
        out = tmp_path / "out"

        assert extract_text.main(["--source", str(source), "--out-dir", str(out),
                                  "--memory", str(tmp_path / "mem.json")]) == 0

        assert (out / "manifest.json").exists()
        assert (out / "glossary_terms.txt").exists()
        assert list(out.glob("part_*.txt"))

    def test_a_full_round_trip_lands_translations(self, workspace):
        tmp_path, source = workspace
        out = tmp_path / "out"
        extract_text.main(["--source", str(source), "--out-dir", str(out),
                           "--memory", str(tmp_path / "mem.json")])

        self._translate(out, lambda line: "ПЕР " + line, {"Fixed Damage": "Фикс. урон"})

        assert apply_text.main([
            "--source", str(source), "--out-dir", str(out),
            "--memory", str(tmp_path / "mem.json"),
            "--output", str(tmp_path / "all_ru.json"),
            "--report", str(tmp_path / "report.json"),
        ]) == 0

        result = json.loads((tmp_path / "all_ru.json").read_text(encoding="utf-8"))
        assert len(result) == len(SAMPLES)
        assert result["Blood Pact"] == "ПЕР Blood Pact"

        # Every marker survived, wherever the translation chose to put it. Position is not the
        # guarantee — presence is, because word order legitimately moves things around.
        rebuilt = result["<#F9385D>Deal $Fixed Damage$ to all</>"]
        assert "<#F9385D>" in rebuilt
        assert "</>" in rebuilt
        assert "$Фикс. урон$" in rebuilt

    def test_seeding_marks_clean_strings_as_done(self, workspace):
        tmp_path, source = workspace
        seed = tmp_path / "clean.json"
        seed.write_text(json.dumps({"Blood Pact": "Кровавый пакт"}, ensure_ascii=False), encoding="utf-8")

        out = tmp_path / "out"
        extract_text.main(["--source", str(source), "--seed", str(seed),
                           "--out-dir", str(out), "--memory", str(tmp_path / "mem.json")])

        memory = json.loads((tmp_path / "mem.json").read_text(encoding="utf-8"))
        assert memory["Blood Pact"] == "Кровавый пакт"

        emitted = {s for part in json.loads((out / "manifest.json").read_text(encoding="utf-8"))["parts"]
                   for s in part["segments"]}
        assert "Blood Pact" not in emitted

    def test_a_seed_entry_hiding_markup_is_rejected(self, workspace):
        """This is how "$Discard Pile$" became "доллары США": a translator that could not see the
        markup was handed a string that had some."""
        tmp_path, source = workspace
        seed = tmp_path / "clean.json"
        seed.write_text(json.dumps({"#basic_ev_0# $Charm$": "мусор"}, ensure_ascii=False), encoding="utf-8")

        extract_text.main(["--source", str(source), "--seed", str(seed),
                           "--out-dir", str(tmp_path / "out"), "--memory", str(tmp_path / "mem.json")])

        memory = json.loads((tmp_path / "mem.json").read_text(encoding="utf-8"))
        assert "мусор" not in memory.values()

    def test_a_line_count_mismatch_skips_that_part(self, workspace):
        tmp_path, source = workspace
        out = tmp_path / "out"
        extract_text.main(["--source", str(source), "--out-dir", str(out),
                           "--memory", str(tmp_path / "mem.json")])

        # Two lines glued into one, as a web translator does with short adjacent lines.
        manifest = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
        part = manifest["parts"][0]
        lines = ["ПЕР " + s for s in part["segments"]]
        lines[0] = lines[0] + " " + lines.pop(1)
        (out / part["file"].replace(".txt", ".ru.txt")).write_text("\n".join(lines) + "\n", encoding="utf-8")

        apply_text.main([
            "--source", str(source), "--out-dir", str(out), "--memory", str(tmp_path / "mem.json"),
            "--output", str(tmp_path / "all_ru.json"), "--report", str(tmp_path / "report.json"),
        ])

        # Nothing from that part is applied — matching by position after a merge would shift
        # every following line onto the wrong segment.
        result = json.loads((tmp_path / "all_ru.json").read_text(encoding="utf-8"))
        assert result["Blood Pact"] == "Blood Pact"

    def test_a_second_extract_emits_nothing_new(self, workspace):
        tmp_path, source = workspace
        out = tmp_path / "out"
        memory = tmp_path / "mem.json"

        extract_text.main(["--source", str(source), "--out-dir", str(out), "--memory", str(memory)])
        self._translate(out, lambda line: "ПЕР " + line)
        apply_text.main(["--source", str(source), "--out-dir", str(out), "--memory", str(memory),
                         "--output", str(tmp_path / "all_ru.json"),
                         "--report", str(tmp_path / "report.json")])

        second = tmp_path / "out2"
        extract_text.main(["--source", str(source), "--out-dir", str(second), "--memory", str(memory)])

        # This is what makes a patch cheap: only genuinely new text is emitted.
        manifest = json.loads((second / "manifest.json").read_text(encoding="utf-8"))
        assert manifest["parts"] == []

    def test_new_strings_after_a_patch_are_the_only_thing_emitted(self, workspace):
        tmp_path, source = workspace
        out = tmp_path / "out"
        memory = tmp_path / "mem.json"

        extract_text.main(["--source", str(source), "--out-dir", str(out), "--memory", str(memory)])
        self._translate(out, lambda line: "ПЕР " + line)
        apply_text.main(["--source", str(source), "--out-dir", str(out), "--memory", str(memory),
                         "--output", str(tmp_path / "all_ru.json"),
                         "--report", str(tmp_path / "report.json")])

        patched = tmp_path / "all_en_v2.json"
        patched.write_text(json.dumps(SAMPLES + ["A brand new quest description"], ensure_ascii=False),
                           encoding="utf-8")

        second = tmp_path / "out2"
        extract_text.main(["--source", str(patched), "--out-dir", str(second), "--memory", str(memory)])

        manifest = json.loads((second / "manifest.json").read_text(encoding="utf-8"))
        emitted = [s for part in manifest["parts"] for s in part["segments"]]
        assert emitted == ["A brand new quest description"]


class TestChunking:
    def test_parts_respect_the_character_budget(self):
        segments = ["x" * 100 for _ in range(50)]
        parts = extract_text.chunk(segments, 1000)

        assert all(sum(len(s) + 1 for s in parts[i]) <= 1000 for i in range(len(parts)))
        assert sum(len(p) for p in parts) == 50

    def test_a_segment_longer_than_the_budget_still_gets_its_own_part(self):
        parts = extract_text.chunk(["y" * 5000, "short"], 1000)

        assert parts[0] == ["y" * 5000]
        assert parts[1] == ["short"]


class TestMarkerPattern:
    @pytest.mark.parametrize(
        "text,expected",
        [
            ("<br>", ["<br>"]),
            ("<#FFAA00>", ["<#FFAA00>"]),
            ("</>", ["</>"]),
            ("{0}", ["{0}"]),
            ("{cal}", ["{cal}"]),
            ("#basic_ev_0#", ["#basic_ev_0#"]),
            ("[shake]", ["[shake]"]),
            ("[/shake]", ["[/shake]"]),
            ("%s", ["%s"]),
            ("$Shield$", ["$Shield$"]),
            ("$Discard Pile$", ["$Discard Pile$"]),
        ],
    )
    def test_every_marker_family_is_recognised(self, text, expected):
        assert MARKER.findall(text) == expected

    @pytest.mark.parametrize("text", ["#tv+#", "#+rev_0_1#", "#dmg_attr.leech_0#"])
    def test_variable_names_may_carry_a_sign_or_a_dot(self, text):
        # Ten strings in the real corpus use these; without them a '#tv+#' would survive into
        # the overlay and be drawn on screen as those literal characters.
        assert MARKER.findall(text) == [text]

    def test_a_variable_name_never_runs_across_prose(self):
        # Spaces stay forbidden, so one '#' cannot reach an unrelated one further along.
        assert MARKER.findall("item #1 and #2 here") == []

    def test_a_bare_dollar_is_not_a_marker(self):
        assert MARKER.findall("I'll pay top dollar for it!") == []
