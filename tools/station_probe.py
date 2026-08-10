#!/usr/bin/env python3
"""Shows exactly what the station returns, to diagnose a run that translates nothing.

    python station_probe.py --station station_work/station.json

Prints the installed model list (so the model name can be checked), the request that station_fill
sends, and the raw reply — batch and single. If the reply is empty, still full of <think>, or not a
JSON array, this is where it shows. stdlib only, so the bundled runtime can run it.
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

from czn.station import (
    DEFAULT_ENDPOINT,
    DEFAULT_MODEL,
    SYSTEM_PROMPT,
    SYSTEM_PROMPT_SINGLE,
    OllamaStation,
    strip_thinking,
)

SAMPLE = ["Attack", "Continue", "[0]Deal [1] damage[2]"]


def get_json(url: str, timeout: float = 15):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def post(endpoint: str, model: str, system: str, prompt: str, think: bool, timeout: float):
    payload = {
        "model": model,
        "system": system,
        "prompt": prompt,
        "stream": False,
        "think": think,
        "options": {"temperature": 0.1, "num_predict": 2048},
    }
    request = urllib.request.Request(
        f"{endpoint}/api/generate",
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def rule(title: str) -> None:
    print("\n" + "=" * 70)
    print(title)
    print("=" * 70)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--station", type=Path, default=Path("station.json"))
    args = parser.parse_args(argv)

    settings = {"endpoint": DEFAULT_ENDPOINT, "model": DEFAULT_MODEL}
    if args.station.exists():
        settings.update(json.loads(args.station.read_text(encoding="utf-8")))
    endpoint = str(settings["endpoint"]).rstrip("/")
    model = str(settings["model"])
    timeout = float(settings.get("timeoutSeconds", 120))

    print(f"Endpoint: {endpoint}")
    print(f"Model in station.json: {model!r}")

    # 1. tags — is the model name exactly right?
    rule("1. Installed models (/api/tags)")
    try:
        tags = get_json(f"{endpoint}/api/tags")
    except urllib.error.URLError as error:
        print(f"  Could not reach the station: {error.reason}")
        return 2
    names = [m.get("name", "") for m in tags.get("models", [])]
    for name in names:
        print(f"  - {name}")
    if model in names:
        print(f"\n  OK: {model!r} matches an installed model exactly.")
    else:
        near = [n for n in names if n.split(":")[0] == model.split(":")[0]]
        print(f"\n  NOTE: {model!r} is not an exact match. Same family: {near or 'none'}")
        print("  Ollama needs the exact tag, e.g. 'qwen3:4b' or 'qwen3:4b-instruct'.")

    # 2. batch reply — the actual failure point
    rule("2. Batch reply (what station_fill parses)")
    prompt = json.dumps(SAMPLE, ensure_ascii=False)
    print(f"We send this prompt (a JSON array of {len(SAMPLE)} strings):\n  {prompt}\n")
    try:
        body = post(endpoint, model, SYSTEM_PROMPT, prompt, think=False, timeout=timeout)
    except urllib.error.URLError as error:
        print(f"  Request failed: {error}")
        return 2
    raw = body.get("response", "")
    print(f"Raw 'response' field ({len(raw)} chars):\n{raw!r}\n")
    stripped = strip_thinking(raw)
    if stripped != raw:
        print(f"After stripping <think>: {stripped!r}\n")
    parsed = OllamaStation._parse_array(raw, len(SAMPLE))
    if parsed is None:
        print("  >>> PARSE FAILED: this reply is not a JSON array of the right length.")
        if "<think>" in raw.lower():
            print("  >>> The reply still contains <think> — the model ignored think=False.")
    else:
        print(f"  >>> PARSED OK: {parsed}")

    # 2b. Same batch, but with qwen3's in-prompt /no_think switch, for Ollama versions that ignore
    # the think field. If this one parses while the one above did not, that switch is the fix.
    rule("2b. Batch reply WITH '/no_think' in the prompt")
    try:
        body2 = post(endpoint, model, SYSTEM_PROMPT, prompt + " /no_think", think=False, timeout=timeout)
        raw2 = body2.get("response", "")
        print(f"Raw 'response' ({len(raw2)} chars):\n{raw2!r}\n")
        parsed2 = OllamaStation._parse_array(raw2, len(SAMPLE))
        print(f"  >>> {'PARSED OK: ' + str(parsed2) if parsed2 else 'PARSE FAILED'}")
    except urllib.error.URLError as error:
        print(f"  Request failed: {error}")

    # 3. single reply
    rule("3. Single-string reply (the fallback path)")
    try:
        one = post(endpoint, model, SYSTEM_PROMPT_SINGLE, SAMPLE[0], think=False, timeout=timeout)
        print(f"Raw: {one.get('response', '')!r}")
    except urllib.error.URLError as error:
        print(f"  Request failed: {error}")

    rule("Done")
    print("Paste everything above back so the cause is clear.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
