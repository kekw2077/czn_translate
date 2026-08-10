"""Talking to a translation station.

A "station" is whatever actually turns English into Russian: a GPU box running Ollama, a home
server reached over Tailscale, or a person with a folder and a web translator. It only ever sees
masked text — ``[0]Deal [1] to all[2]`` — so it cannot damage game markup even in principle. All
it has to do is keep the numbered markers, and that is checked on the way back.

Two transports, one interface:

* :class:`OllamaStation` — HTTP. The host is just a URL, so localhost and a machine across
  Tailscale are the same thing (TZ §7 already puts Ollama behind Tailscale).
* :class:`FolderStation` — a drop folder. For when there is no network path at all: the station
  picks files up, translates, writes the answers back beside them.
"""

from __future__ import annotations

import json
import re
import time
import urllib.error
import urllib.request
from abc import ABC, abstractmethod
from dataclasses import dataclass
from pathlib import Path

from .segment import SENTINEL_RE

DEFAULT_ENDPOINT = "http://127.0.0.1:11434"
DEFAULT_MODEL = "qwen2.5:7b-instruct"

SYSTEM_PROMPT = """Ты профессиональный переводчик игровых интерфейсов EN→RU.

В тексте встречаются маркеры вида [0] [1] [2]. Это не текст — это метки, на месте которых
потом появятся элементы оформления.

Правила:
• КАЖДАЯ метка из строки обязана присутствовать в переводе, ровно один раз, в том же написании
• Метку можно переставить туда, где её требует русский порядок слов
• Ничего не дописывай внутрь скобок и не переводи их содержимое
• Интерфейсные строки — кратко, без точки в конце
• Диалоги — нормальная пунктуация

Вход: JSON-массив из N строк. Ответ: ТОЛЬКО JSON-массив из N переводов в том же порядке."""

SYSTEM_PROMPT_SINGLE = """Ты профессиональный переводчик игровых интерфейсов EN→RU.
Переведи одну строку. Метки [0] [1] сохрани дословно, каждую ровно один раз.
Ответь ТОЛЬКО переводом, без кавычек и пояснений."""

_FENCE = re.compile(r"^\s*```(?:json)?\s*(.*?)\s*```\s*$", re.DOTALL)

# Reasoning models (qwen3 among them) emit a <think>...</think> block before the answer. It is slow
# to generate on CPU and its prose is full of brackets, so it wrecks the JSON-array extraction. We
# ask the model to skip it (think=False on the request) and strip it here as a belt-and-braces.
_THINK = re.compile(r"<think>.*?</think>", re.DOTALL | re.IGNORECASE)


def strip_thinking(text: str) -> str:
    """Drops a <think>...</think> block, and any stray opener that never closed."""
    text = _THINK.sub("", text)
    lead = text.lower().find("<think>")
    if lead != -1 and "</think>" not in text.lower():
        text = text[:lead]
    return text.strip()


def sentinels(text: str) -> list[str]:
    """Sorted marker indices in a string — the thing that must match between source and result."""
    return sorted(SENTINEL_RE.findall(text))


def keeps_sentinels(source: str, translated: str) -> bool:
    return sentinels(source) == sentinels(translated)


@dataclass
class StationResult:
    translations: dict[str, str]
    rejected: list[str]

    @property
    def ok(self) -> int:
        return len(self.translations)


class Station(ABC):
    """Anything that can turn a list of masked segments into translations."""

    @abstractmethod
    def describe(self) -> str:
        ...

    @abstractmethod
    def check(self) -> tuple[bool, str]:
        """Is the station reachable and ready? Returns (ok, human-readable detail)."""

    @abstractmethod
    def translate(self, segments: list[str]) -> StationResult:
        ...


class OllamaStation(Station):
    """Ollama over HTTP, local or remote.

    Because the input is already masked, the model never sees a colour tag or a placeholder and
    the only thing to verify is that the ``[n]`` markers came back. That check is exact, so a bad
    translation is caught rather than written into the base.
    """

    def __init__(
        self,
        endpoint: str = DEFAULT_ENDPOINT,
        model: str = DEFAULT_MODEL,
        batch: int = 25,
        timeout: float = 300.0,
        retries: int = 2,
        num_thread: int | None = None,
    ) -> None:
        self.endpoint = endpoint.rstrip("/")
        self.model = model
        self.batch = max(1, batch)
        self.timeout = timeout
        self.retries = max(1, retries)
        # None lets Ollama pick; set it to the core count to push a CPU box to full utilisation.
        self.num_thread = num_thread

    def describe(self) -> str:
        return f"Ollama {self.model} at {self.endpoint} (batch {self.batch})"

    def check(self) -> tuple[bool, str]:
        try:
            with urllib.request.urlopen(f"{self.endpoint}/api/tags", timeout=10) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except urllib.error.URLError as error:
            return False, f"unreachable: {error.reason}"
        except (TimeoutError, json.JSONDecodeError) as error:
            return False, f"bad response: {error}"

        available = [m.get("name", "") for m in payload.get("models", [])]
        if not any(self.model.split(":")[0] in name for name in available):
            listing = ", ".join(available[:8]) or "none"
            return False, f"model '{self.model}' not present. Available: {listing}"

        return True, f"reachable, {len(available)} model(s) installed"

    def _generate(self, system: str, prompt: str, predict: int) -> str:
        options: dict = {"temperature": 0.1, "num_predict": predict}
        if self.num_thread:
            options["num_thread"] = self.num_thread

        payload = {
            "model": self.model,
            "system": system,
            "prompt": prompt,
            "stream": False,
            # Turn off reasoning for models that support it (qwen3). Ollama ignores the field for
            # models that do not, so it is safe to always send.
            "think": False,
            "options": options,
        }
        request = urllib.request.Request(
            f"{self.endpoint}/api/generate",
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            return json.loads(response.read().decode("utf-8")).get("response", "")

    @staticmethod
    def _parse_array(raw: str, expected: int) -> list[str] | None:
        text = strip_thinking(raw)
        fenced = _FENCE.match(text)
        if fenced:
            text = fenced.group(1)

        start, end = text.find("["), text.rfind("]") + 1
        if start < 0 or end <= start:
            return None

        try:
            parsed = json.loads(text[start:end])
        except json.JSONDecodeError:
            return None

        if not isinstance(parsed, list) or len(parsed) != expected:
            return None
        if not all(isinstance(item, str) for item in parsed):
            return None

        return parsed

    def translate(self, segments: list[str]) -> StationResult:
        translations: dict[str, str] = {}
        rejected: list[str] = []

        for start in range(0, len(segments), self.batch):
            group = segments[start:start + self.batch]
            leftovers = self._translate_group(group, translations)
            rejected.extend(leftovers)

        return StationResult(translations, rejected)

    def _translate_group(self, group: list[str], sink: dict[str, str]) -> list[str]:
        # Output budget scales with the input; Russian runs longer than English and a truncated
        # reply is unparseable JSON, which would throw away the whole batch.
        predict = max(1024, int(sum(len(s) for s in group) * 1.6))

        parsed = None
        try:
            parsed = self._parse_array(
                self._generate(SYSTEM_PROMPT, json.dumps(group, ensure_ascii=False), predict),
                len(group),
            )
        except (urllib.error.URLError, TimeoutError):
            parsed = None

        failures: list[str] = []
        if parsed is None:
            failures = list(group)
        else:
            for source, translated in zip(group, parsed):
                if translated.strip() and keeps_sentinels(source, translated):
                    sink[source] = translated.strip()
                else:
                    failures.append(source)

        # One at a time for whatever the batch could not manage. Short segments dominate, so this
        # is cheap and recovers most of the tail.
        still_bad: list[str] = []
        for source in failures:
            for _ in range(self.retries):
                try:
                    single = strip_thinking(self._generate(SYSTEM_PROMPT_SINGLE, source, 1024)).strip('"')
                except (urllib.error.URLError, TimeoutError):
                    continue
                if single and keeps_sentinels(source, single):
                    sink[source] = single
                    break
            else:
                still_bad.append(source)

        return still_bad


class FolderStation(Station):
    """A drop folder, for a station with no network path to here.

    Writes ``request_NNN.txt`` and waits for ``request_NNN.ru.txt`` to appear beside it with the
    same number of lines. That is the same contract as the manual web-translator route, so the
    same folder works whether a script or a person is on the other end.
    """

    def __init__(self, folder: Path, poll_seconds: float = 5.0, wait_seconds: float = 0.0) -> None:
        self.folder = Path(folder)
        self.poll_seconds = poll_seconds
        self.wait_seconds = wait_seconds

    def describe(self) -> str:
        return f"drop folder {self.folder}"

    def check(self) -> tuple[bool, str]:
        try:
            self.folder.mkdir(parents=True, exist_ok=True)
        except OSError as error:
            return False, f"cannot create {self.folder}: {error}"

        pending = sorted(self.folder.glob("request_*.txt"))
        answered = sum(1 for p in pending if p.with_suffix(".ru.txt").exists())
        return True, f"{len(pending)} request(s) present, {answered} answered"

    def translate(self, segments: list[str]) -> StationResult:
        self.folder.mkdir(parents=True, exist_ok=True)

        index = 1
        while (self.folder / f"request_{index:03d}.txt").exists():
            index += 1

        request = self.folder / f"request_{index:03d}.txt"
        answer = self.folder / f"request_{index:03d}.ru.txt"
        request.write_text("\n".join(segments) + "\n", encoding="utf-8")

        deadline = time.monotonic() + self.wait_seconds
        while not answer.exists() and time.monotonic() < deadline:
            time.sleep(self.poll_seconds)

        if not answer.exists():
            # Not an error: the station works on its own schedule. The request stays on disk and
            # the next run picks the answer up.
            return StationResult({}, list(segments))

        return StationResult(*self._read_answer(request, answer))

    @staticmethod
    def _read_answer(request: Path, answer: Path) -> tuple[dict[str, str], list[str]]:
        sources = request.read_text(encoding="utf-8-sig").split("\n")
        results = answer.read_text(encoding="utf-8-sig").split("\n")

        if sources and sources[-1] == "":
            sources.pop()
        if results and results[-1] == "":
            results.pop()

        if len(sources) != len(results):
            # Positional matching is all a text file supports, so a line count that drifted means
            # every line after the drift would land on the wrong segment.
            return {}, sources

        translations: dict[str, str] = {}
        rejected: list[str] = []
        for source, translated in zip(sources, results):
            translated = translated.strip()
            if translated and keeps_sentinels(source, translated):
                translations[source] = translated
            else:
                rejected.append(source)

        return translations, rejected


def build_station(settings: dict) -> Station:
    """Builds a station from a config dict. ``kind`` is ``ollama`` or ``folder``."""
    kind = str(settings.get("kind", "ollama")).lower()

    if kind == "ollama":
        num_thread = settings.get("numThread")
        return OllamaStation(
            endpoint=settings.get("endpoint", DEFAULT_ENDPOINT),
            model=settings.get("model", DEFAULT_MODEL),
            batch=int(settings.get("batch", 25)),
            timeout=float(settings.get("timeoutSeconds", 300)),
            retries=int(settings.get("retries", 2)),
            num_thread=int(num_thread) if num_thread else None,
        )

    if kind == "folder":
        return FolderStation(
            folder=Path(settings.get("folder", "station_dropbox")),
            poll_seconds=float(settings.get("pollSeconds", 5)),
            wait_seconds=float(settings.get("waitSeconds", 0)),
        )

    raise ValueError(f"unknown station kind '{kind}' (expected 'ollama' or 'folder')")
