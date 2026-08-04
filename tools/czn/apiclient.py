"""Batch translation client for a hosted LLM API (TZ §8).

A drop-in sibling of ``czn.ollama.OllamaClient`` — same ``translate_batch(items, glossary)``
contract, same strict JSON protocol (``build_prompt`` / ``parse_response`` are shared), so
``translate.py`` treats the two the same. Two providers are supported:

* ``anthropic`` — the Messages API (``/v1/messages``, ``x-api-key`` header).
* ``openai``    — any OpenAI-compatible ``/chat/completions`` endpoint (``Authorization: Bearer``),
                  which also covers Together, Groq, OpenRouter, a local vLLM, … via ``base_url``.

Only the standard library is used, matching ``ollama.py``; there is no SDK dependency. Token
usage is accumulated across calls so the batch run can print what it cost.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field

from .ollama import (
    SYSTEM_PROMPT,
    BatchTranslationError,
    TranslationItem,
    build_prompt,
    parse_response,
    render_glossary,
)

# Localization is deterministic work; keep sampling low so re-runs converge.
TEMPERATURE = 0.2

# Generous headroom for the JSON array of a 40-string batch; short UI text stays far under this.
MAX_OUTPUT_TOKENS = 8192

DEFAULT_MODELS = {
    "anthropic": "claude-haiku-4-5-20251001",
    "openai": "gpt-4o-mini",
}

DEFAULT_BASE_URLS = {
    "anthropic": "https://api.anthropic.com",
    "openai": "https://api.openai.com/v1",
}

# HTTP statuses worth waiting out rather than failing the batch on.
_RETRYABLE = {429, 500, 502, 503, 529}


@dataclass
class Usage:
    input_tokens: int = 0
    output_tokens: int = 0
    calls: int = 0

    def add(self, other: "Usage") -> None:
        self.input_tokens += other.input_tokens
        self.output_tokens += other.output_tokens
        self.calls += other.calls


class ApiClient:
    def __init__(
        self,
        provider: str,
        api_key: str,
        model: str | None = None,
        base_url: str | None = None,
        timeout: float = 120.0,
        max_http_retries: int = 5,
    ) -> None:
        if provider not in DEFAULT_MODELS:
            raise ValueError(f"unknown provider {provider!r}; expected one of {sorted(DEFAULT_MODELS)}")
        if not api_key:
            raise ValueError("api_key is empty — set the key env var or put it in tools/.env")

        self.provider = provider
        self.api_key = api_key
        self.model = model or DEFAULT_MODELS[provider]
        self.base_url = (base_url or DEFAULT_BASE_URLS[provider]).rstrip("/")
        self.timeout = timeout
        self.max_http_retries = max_http_retries
        self.usage = Usage()

    # ---------------------------------------------------------------- transport

    def _post(self, url: str, headers: dict[str, str], body: dict) -> dict:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        request = urllib.request.Request(url, data=data, headers=headers, method="POST")

        delay = 1.0
        last_error: Exception | None = None
        for attempt in range(self.max_http_retries):
            try:
                with urllib.request.urlopen(request, timeout=self.timeout) as response:
                    return json.loads(response.read().decode("utf-8"))
            except urllib.error.HTTPError as error:
                # 4xx other than rate limiting is a request bug — surface it, do not hammer.
                if error.code not in _RETRYABLE:
                    detail = error.read().decode("utf-8", "replace")[:500]
                    raise BatchTranslationError(f"HTTP {error.code} from {self.provider}: {detail}") from error
                last_error = error
            except (urllib.error.URLError, TimeoutError) as error:
                last_error = error

            if attempt < self.max_http_retries - 1:
                time.sleep(delay)
                delay = min(delay * 2, 30.0)

        raise BatchTranslationError(f"{self.provider} unreachable after {self.max_http_retries} tries: {last_error}")

    # ------------------------------------------------------------ provider glue

    def _generate(self, system: str, prompt: str) -> tuple[str, Usage]:
        if self.provider == "anthropic":
            body = self._post(
                f"{self.base_url}/v1/messages",
                {
                    "x-api-key": self.api_key,
                    "anthropic-version": "2023-06-01",
                    "content-type": "application/json",
                },
                {
                    "model": self.model,
                    "max_tokens": MAX_OUTPUT_TOKENS,
                    "temperature": TEMPERATURE,
                    "system": system,
                    "messages": [{"role": "user", "content": prompt}],
                },
            )
            parts = [b.get("text", "") for b in body.get("content", []) if b.get("type") == "text"]
            usage = body.get("usage", {})
            return "".join(parts), Usage(usage.get("input_tokens", 0), usage.get("output_tokens", 0), 1)

        # openai-compatible
        body = self._post(
            f"{self.base_url}/chat/completions",
            {"Authorization": f"Bearer {self.api_key}", "content-type": "application/json"},
            {
                "model": self.model,
                "temperature": TEMPERATURE,
                "messages": [
                    {"role": "system", "content": system},
                    {"role": "user", "content": prompt},
                ],
            },
        )
        text = body.get("choices", [{}])[0].get("message", {}).get("content", "") or ""
        usage = body.get("usage", {})
        return text, Usage(usage.get("prompt_tokens", 0), usage.get("completion_tokens", 0), 1)

    # ---------------------------------------------------------------- interface

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
            text, usage = self._generate(system, build_prompt(items))
            self.usage.add(usage)
            try:
                return parse_response(text, expected)
            except BatchTranslationError as error:
                # Transport already retried inside _post; a parse failure means the model wrote
                # something malformed, so re-ask (temperature>0 gives a different draw).
                last_error = error

        raise BatchTranslationError(f"batch failed after {attempts} attempts: {last_error}")
