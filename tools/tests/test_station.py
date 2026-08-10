"""The station link: HTTP transport, folder transport, and the sentinel check on the way back."""

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

import translate_station
from czn.station import (
    FolderStation,
    OllamaStation,
    build_station,
    keeps_sentinels,
    sentinels,
)


class FakeOllama:
    """A real HTTP server that answers like Ollama, so the transport is exercised end to end."""

    def __init__(self, behaviour="echo", models=("qwen2.5:7b-instruct",)):
        self.behaviour = behaviour
        self.models = list(models)
        self.batch_calls = 0
        self.single_calls = 0
        self.last_think = "unset"  # records the request's think flag, to prove it is turned off

        station = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _send(self, payload):
                body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                if self.path == "/api/tags":
                    self._send({"models": [{"name": m} for m in station.models]})
                else:
                    self.send_error(404)

            def do_POST(self):
                length = int(self.headers.get("Content-Length", "0"))
                request = json.loads(self.rfile.read(length).decode("utf-8"))
                self._send({"response": station.respond(request)})

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.endpoint = f"http://127.0.0.1:{self._server.server_port}"
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    def respond(self, request):
        prompt = request["prompt"]
        self.last_think = request.get("think", "absent")

        # A reasoning model prepends a <think> block whose prose is full of brackets — exactly what
        # trips the array extraction. The station asks it off and strips it either way.
        think = "<think>Hmm, [0] is a marker, let me keep it. The list has several items.</think>\n"

        # Distinguish by the system prompt, not by the payload: a single segment can itself
        # start with "[0]", which is exactly what a leading-bracket check gets wrong.
        is_batch = "JSON-массив" in request.get("system", "")

        if is_batch:
            self.batch_calls += 1
            items = json.loads(prompt)
            if self.behaviour == "garbage":
                return "I am afraid I cannot do that."
            if self.behaviour == "short":
                items = items[:-1]
            if self.behaviour == "drops-sentinel":
                return json.dumps([s.replace("[0]", "", 1) for s in items], ensure_ascii=False)
            body = json.dumps([f"РУ {s}" for s in items], ensure_ascii=False)
            return think + body if self.behaviour == "thinks" else body

        self.single_calls += 1
        if self.behaviour in ("garbage", "short"):
            return f"РУ {prompt}"
        if self.behaviour == "drops-sentinel":
            return prompt.replace("[0]", "", 1)
        return (think + f"РУ {prompt}") if self.behaviour == "thinks" else f"РУ {prompt}"

    def stop(self):
        self._server.shutdown()
        self._server.server_close()


@pytest.fixture
def fake():
    server = FakeOllama()
    yield server
    server.stop()


class TestSentinelCheck:
    def test_extraction_and_comparison(self):
        assert sentinels("[1] и [0]") == ["0", "1"]
        assert keeps_sentinels("[0] a [1]", "[1] б [0]")
        assert not keeps_sentinels("[0] a [1]", "[0] б")

    def test_a_duplicated_marker_is_rejected(self):
        # Emitting [0] twice would paste the same colour tag in two places.
        assert not keeps_sentinels("[0] a", "[0] б [0]")


class TestOllamaStation:
    def test_check_reports_a_healthy_station(self, fake):
        ok, detail = OllamaStation(fake.endpoint).check()
        assert ok
        assert "model" in detail or "reachable" in detail

    def test_check_reports_an_unreachable_station(self):
        ok, detail = OllamaStation("http://127.0.0.1:1", timeout=2).check()
        assert not ok
        assert "unreachable" in detail

    def test_check_reports_a_missing_model(self, fake):
        ok, detail = OllamaStation(fake.endpoint, model="llama-nonexistent").check()
        assert not ok
        assert "not installed" in detail

    def test_check_rejects_a_family_match_that_is_not_the_exact_tag(self, fake):
        # 'qwen2.5:7b' is the same family as the installed 'qwen2.5:7b-instruct' but a different tag,
        # so generation would 404 — the check must fail rather than wave it through.
        ok, detail = OllamaStation(fake.endpoint, model="qwen2.5:7b").check()
        assert not ok
        assert "qwen2.5:7b-instruct" in detail  # the installed tags are listed for the user

    def test_translates_a_batch(self, fake):
        result = OllamaStation(fake.endpoint, batch=10).translate(["[0] Attack", "Defend"])

        assert result.translations["[0] Attack"] == "РУ [0] Attack"
        assert result.rejected == []

    def test_batches_respect_the_size(self, fake):
        OllamaStation(fake.endpoint, batch=2).translate([f"line {i}" for i in range(6)])
        assert fake.batch_calls == 3

    def test_a_dropped_marker_falls_back_to_single_then_is_rejected(self, fake):
        fake.behaviour = "drops-sentinel"
        result = OllamaStation(fake.endpoint, batch=5, retries=2).translate(["[0] Attack"])

        # Never silently kept: a translation missing its marker would lose a colour tag.
        assert result.translations == {}
        assert result.rejected == ["[0] Attack"]
        assert fake.single_calls == 2

    def test_unparseable_output_falls_back_to_single_items(self, fake):
        fake.behaviour = "garbage"
        result = OllamaStation(fake.endpoint, batch=5).translate(["[0] Attack", "Defend"])

        assert result.ok == 2
        assert fake.single_calls == 2

    def test_a_short_array_falls_back_rather_than_misaligning(self, fake):
        # Zipping a 2-item reply onto 3 sources would attach translations to the wrong strings.
        fake.behaviour = "short"
        result = OllamaStation(fake.endpoint, batch=5).translate(["a", "b", "c"])

        assert result.ok == 3
        assert fake.single_calls == 3

    def test_an_unreachable_station_rejects_everything_without_raising(self):
        result = OllamaStation("http://127.0.0.1:1", timeout=2, retries=1).translate(["a", "b"])

        assert result.translations == {}
        assert sorted(result.rejected) == ["a", "b"]

    def test_a_reasoning_block_is_stripped_and_the_batch_still_lands(self, fake):
        # qwen3 wraps its answer in <think>...</think>; without stripping, every batch would fail.
        fake.behaviour = "thinks"
        result = OllamaStation(fake.endpoint, batch=5).translate(["[0] Attack", "Defend"])

        assert result.translations["[0] Attack"] == "РУ [0] Attack"
        assert result.rejected == []
        assert fake.single_calls == 0  # the batch parsed on the first try, no fallback needed

    def test_thinking_is_turned_off_on_the_request(self, fake):
        OllamaStation(fake.endpoint, batch=5).translate(["Attack"])
        assert fake.last_think is False

    def test_num_thread_is_forwarded_to_the_options(self, fake):
        # Proxy check: with a healthy fake the batch still lands when num_thread is set.
        result = OllamaStation(fake.endpoint, batch=5, num_thread=8).translate(["Attack"])
        assert result.translations == {"Attack": "РУ Attack"}


class TestFolderStation:
    def test_check_creates_the_folder(self, tmp_path):
        ok, _ = FolderStation(tmp_path / "drop").check()
        assert ok
        assert (tmp_path / "drop").is_dir()

    def test_a_request_is_written_and_left_for_the_station(self, tmp_path):
        result = FolderStation(tmp_path / "drop").translate(["[0] Attack", "Defend"])

        request = tmp_path / "drop" / "request_001.txt"
        assert request.read_text(encoding="utf-8").split("\n")[:-1] == ["[0] Attack", "Defend"]

        # Nothing is lost: the segments come back as rejected and the next run collects the answer.
        assert result.translations == {}
        assert result.rejected == ["[0] Attack", "Defend"]

    def test_an_answer_left_by_the_station_is_picked_up(self, tmp_path):
        drop = tmp_path / "drop"
        station = FolderStation(drop)
        station.translate(["[0] Attack", "Defend"])

        (drop / "request_001.ru.txt").write_text("[0] Атака\nЗащита\n", encoding="utf-8")

        result = FolderStation(drop)._read_answer(
            drop / "request_001.txt", drop / "request_001.ru.txt"
        )
        assert result[0] == {"[0] Attack": "[0] Атака", "Defend": "Защита"}

    def test_a_line_count_mismatch_rejects_the_whole_file(self, tmp_path):
        drop = tmp_path / "drop"
        drop.mkdir()
        (drop / "request_001.txt").write_text("a\nb\nc\n", encoding="utf-8")
        (drop / "request_001.ru.txt").write_text("А\nБ\n", encoding="utf-8")

        translations, rejected = FolderStation(drop)._read_answer(
            drop / "request_001.txt", drop / "request_001.ru.txt"
        )

        assert translations == {}
        assert rejected == ["a", "b", "c"]

    def test_a_lost_marker_is_rejected_line_by_line(self, tmp_path):
        drop = tmp_path / "drop"
        drop.mkdir()
        (drop / "request_001.txt").write_text("[0] a\n[0] b\n", encoding="utf-8")
        (drop / "request_001.ru.txt").write_text("[0] А\nБ\n", encoding="utf-8")

        translations, rejected = FolderStation(drop)._read_answer(
            drop / "request_001.txt", drop / "request_001.ru.txt"
        )

        assert translations == {"[0] a": "[0] А"}
        assert rejected == ["[0] b"]

    def test_requests_do_not_overwrite_each_other(self, tmp_path):
        drop = tmp_path / "drop"
        FolderStation(drop).translate(["first"])
        FolderStation(drop).translate(["second"])

        assert (drop / "request_001.txt").read_text(encoding="utf-8").strip() == "first"
        assert (drop / "request_002.txt").read_text(encoding="utf-8").strip() == "second"


class TestBuildStation:
    def test_builds_ollama_by_default(self):
        assert isinstance(build_station({}), OllamaStation)

    def test_builds_a_folder_station(self, tmp_path):
        station = build_station({"kind": "folder", "folder": str(tmp_path)})
        assert isinstance(station, FolderStation)

    def test_an_unknown_kind_is_rejected(self):
        with pytest.raises(ValueError, match="deepl"):
            build_station({"kind": "deepl"})

    def test_settings_are_carried_through(self):
        station = build_station(
            {"kind": "ollama", "endpoint": "http://station:11434/", "model": "m", "batch": 7}
        )
        assert station.endpoint == "http://station:11434"
        assert station.batch == 7


class TestCli:
    @pytest.fixture
    def workspace(self, tmp_path):
        source = tmp_path / "all_en.json"
        source.write_text(
            json.dumps(["<#F00>Deal $Shield$ damage</>", "Plain sentence here", "12345"],
                       ensure_ascii=False),
            encoding="utf-8",
        )
        return tmp_path, source

    def _station_file(self, tmp_path, endpoint):
        path = tmp_path / "station.json"
        path.write_text(json.dumps({"kind": "ollama", "endpoint": endpoint, "batch": 10}),
                        encoding="utf-8")
        return path

    def test_check_only(self, tmp_path, fake, capsys):
        assert translate_station.main(
            ["--check", "--station", str(self._station_file(tmp_path, fake.endpoint))]
        ) == 0
        assert "reachable" in capsys.readouterr().out

    def test_check_fails_on_a_dead_station(self, tmp_path, capsys):
        path = tmp_path / "station.json"
        path.write_text(json.dumps({"kind": "ollama", "endpoint": "http://127.0.0.1:1"}),
                        encoding="utf-8")

        assert translate_station.main(["--check", "--station", str(path)]) == 1

    def test_fills_the_memory(self, workspace, fake):
        tmp_path, source = workspace
        memory = tmp_path / "mem.json"

        assert translate_station.main([
            "--source", str(source),
            "--station", str(self._station_file(tmp_path, fake.endpoint)),
            "--memory", str(memory),
        ]) == 0

        stored = json.loads(memory.read_text(encoding="utf-8"))
        # The pure-number string has nothing to translate and never reaches the station.
        assert "12345" not in stored
        assert stored["Plain sentence here"] == "РУ Plain sentence here"
        assert stored["[0]Deal [1] damage[2]"] == "РУ [0]Deal [1] damage[2]"

    def test_a_second_run_has_nothing_to_do(self, workspace, fake, capsys):
        tmp_path, source = workspace
        memory = tmp_path / "mem.json"
        station = self._station_file(tmp_path, fake.endpoint)

        translate_station.main(["--source", str(source), "--station", str(station),
                                "--memory", str(memory)])
        calls = fake.batch_calls

        translate_station.main(["--source", str(source), "--station", str(station),
                                "--memory", str(memory)])

        assert fake.batch_calls == calls
        assert "Nothing to translate" in capsys.readouterr().out

    def test_rejected_segments_are_written_out(self, workspace, fake):
        tmp_path, source = workspace
        fake.behaviour = "drops-sentinel"
        failed = tmp_path / "failed.txt"

        translate_station.main([
            "--source", str(source),
            "--station", str(self._station_file(tmp_path, fake.endpoint)),
            "--memory", str(tmp_path / "mem.json"),
            "--failed", str(failed),
        ])

        assert "[0]Deal [1] damage[2]" in failed.read_text(encoding="utf-8")

    def test_limit_caps_the_work(self, workspace, fake):
        tmp_path, source = workspace
        memory = tmp_path / "mem.json"

        translate_station.main([
            "--source", str(source),
            "--station", str(self._station_file(tmp_path, fake.endpoint)),
            "--memory", str(memory), "--limit", "1",
        ])

        assert len(json.loads(memory.read_text(encoding="utf-8"))) == 1
