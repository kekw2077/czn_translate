"""Batch translation client for Ollama (TZ §8).

Batches of 40, JSON in and JSON out. An invalid JSON reply sends the whole batch back for a
retry rather than trying to salvage part of it — a half-parsed batch silently drops strings, and
a dropped string looks exactly like an untranslated one three steps later.
"""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.request
from dataclasses import dataclass

DEFAULT_ENDPOINT = "http://127.0.0.1:11434"
DEFAULT_MODEL = "qwen3-loc"
BATCH_SIZE = 40

SYSTEM_PROMPT = """Ты переводчик игровой локализации. Переводишь с английского на русский
интерфейс и сюжетные тексты тёмного фэнтези-рогалика.

Правила:
- Сохраняй ВСЕ плейсхолдеры ({0}, {value}, %s) и теги (<color=...>, </color>,
  <sprite=...>) точно как в оригинале, включая порядок.
- Соблюдай глоссарий дословно, без склонений имён собственных в базовой форме.
- Интерфейсные строки — коротко, в стиле игровых кнопок, без точки в конце.
- Не добавляй пояснений, не переводи то, что выглядит как техническое ID.
- Если строка непереводима (код, число, пустая) — верни её без изменений.

Глоссарий:
{glossary}

Отвечай ТОЛЬКО валидным JSON-массивом вида [{{"id": <int>, "ru": "<перевод>"}}].
Без markdown, без пояснений."""

# Models wrap JSON in a fenced block often enough to be worth handling rather than retrying.
_FENCE = re.compile(r"^\s*```(?:json)?\s*(.*?)\s*```\s*$", re.DOTALL)


class BatchTranslationError(RuntimeError):
    """The reply could not be turned into id → translation pairs; the batch must be retried."""


@dataclass
class TranslationItem:
    id: int
    en: str


def render_glossary(glossary: dict[str, str]) -> str:
    if not glossary:
        return "(пусто)"
    return "\n".join(f"- {en} = {ru}" for en, ru in sorted(glossary.items()))


def build_prompt(items: list[TranslationItem]) -> str:
    payload = [{"id": item.id, "en": item.en} for item in items]
    return json.dumps(payload, ensure_ascii=False, indent=None)


def parse_response(text: str, expected_ids: set[int]) -> dict[int, str]:
    """Strict parse: every requested id must come back, and nothing extra.

    Accepting a partial answer would leave strings silently untranslated, which is
    indistinguishable downstream from strings the model chose to pass through unchanged.
    """
    stripped = text.strip()
    fenced = _FENCE.match(stripped)
    if fenced:
        stripped = fenced.group(1)

    try:
        parsed = json.loads(stripped)
    except json.JSONDecodeError as error:
        raise BatchTranslationError(f"reply is not valid JSON: {error}") from error

    if not isinstance(parsed, list):
        raise BatchTranslationError(f"expected a JSON array, got {type(parsed).__name__}")

    result: dict[int, str] = {}
    for entry in parsed:
        if not isinstance(entry, dict) or "id" not in entry or "ru" not in entry:
            raise BatchTranslationError(f"malformed entry: {entry!r}")
        try:
            entry_id = int(entry["id"])
        except (TypeError, ValueError) as error:
            raise BatchTranslationError(f"non-integer id: {entry['id']!r}") from error

        if entry_id not in expected_ids:
            raise BatchTranslationError(f"reply contains unrequested id {entry_id}")
        if not isinstance(entry["ru"], str):
            raise BatchTranslationError(f"translation for {entry_id} is not a string")

        result[entry_id] = entry["ru"]

    missing = expected_ids - result.keys()
    if missing:
        raise BatchTranslationError(f"reply is missing {len(missing)} id(s): {sorted(missing)[:5]}")

    return result


class OllamaClient:
    def __init__(
        self,
        endpoint: str = DEFAULT_ENDPOINT,
        model: str = DEFAULT_MODEL,
        timeout: float = 180.0,
    ) -> None:
        self.endpoint = endpoint.rstrip("/")
        self.model = model
        self.timeout = timeout

    def generate(self, system: str, prompt: str) -> str:
        request = urllib.request.Request(
            f"{self.endpoint}/api/generate",
            data=json.dumps(
                {
                    "model": self.model,
                    "system": system,
                    "prompt": prompt,
                    "stream": False,
                    # Localization wants repeatability across runs, not variety.
                    "options": {"temperature": 0.2},
                },
                ensure_ascii=False,
            ).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )

        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            body = json.loads(response.read().decode("utf-8"))

        return body.get("response", "")

    def translate_batch(
        self,
        items: list[TranslationItem],
        glossary: dict[str, str],
        attempts: int = 2,
    ) -> dict[int, str]:
        if not items:
            return {}

        system = SYSTEM_PROMPT.format(glossary=render_glossary(glossary))
        expected = {item.id for item in items}
        last_error: Exception | None = None

        for _ in range(attempts):
            try:
                return parse_response(self.generate(system, build_prompt(items)), expected)
            except (BatchTranslationError, urllib.error.URLError, TimeoutError) as error:
                last_error = error

        raise BatchTranslationError(f"batch failed after {attempts} attempts: {last_error}")


def chunk(items: list[TranslationItem], size: int = BATCH_SIZE) -> list[list[TranslationItem]]:
    return [items[start:start + size] for start in range(0, len(items), size)]
