#!/usr/bin/env python3
"""CZN Translator control panel — one local page for the whole offline conveyor (TZ §8).

    python panel.py --db ../czn.db        # then open http://127.0.0.1:8777

A single loopback web app, standard library only, that gathers every operator task in one
Nexus-styled window: set the API key, run the translation batch with live progress, review the
machine output, and pull in a game patch. It drives the same scripts (translate.py, diff_pack.py)
and the same czn.db as the command line — the panel is a front end, not a second implementation.

No authentication: it binds 127.0.0.1 only and edits the base directly, so it must never be
exposed off the machine.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import threading
import time
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from czn.db import Database
from czn.validate import validate

HERE = Path(__file__).resolve().parent
ENV_PATH = HERE / ".env"
TRANSLATE = HERE / "translate.py"
DIFF = HERE / "diff_pack.py"

# The re-extractor and its output live under extracted/ (gitignored — game content stays local).
EXTRACT = HERE.parent / "extracted" / "scripts" / "extract_pack.py"
PAIRS_OUT = HERE.parent / "extracted" / "text" / "en.pairs.json"
DEFAULT_PACK = r"C:\ProgramData\Smilegate\Games\ChaosZeroNightmare\bin\appdata\cznlive\data.pack"

KEY_ENV = {"anthropic": "ANTHROPIC_API_KEY", "openai": "OPENAI_API_KEY"}

REVIEW_PAGE_SIZE = 50

# Imported here so the page can show the same asset without a second web request.
from panel_assets import PAGE  # noqa: E402


# --------------------------------------------------------------------------- .env

def read_env() -> dict[str, str]:
    values: dict[str, str] = {}
    if ENV_PATH.exists():
        for line in ENV_PATH.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            name, value = line.split("=", 1)
            values[name.strip()] = value.strip().strip('"').strip("'")
    return values


def write_env_key(name: str, value: str) -> None:
    """Upsert one KEY=value, leaving every other line (comments included) untouched."""
    lines = ENV_PATH.read_text(encoding="utf-8").splitlines() if ENV_PATH.exists() else []
    out: list[str] = []
    replaced = False
    for line in lines:
        stripped = line.strip()
        if stripped and not stripped.startswith("#") and "=" in stripped and stripped.split("=", 1)[0].strip() == name:
            out.append(f"{name}={value}")
            replaced = True
        else:
            out.append(line)
    if not replaced:
        out.append(f"{name}={value}")
    ENV_PATH.write_text("\n".join(out) + "\n", encoding="utf-8")


# ----------------------------------------------------------------------- job runner

class TranslationJob:
    """Runs translate.py as a subprocess and exposes its progress to the page.

    A subprocess (not an in-process call) keeps the batch on its own SQLite connection and lets a
    crash stay contained; the page polls /api/job while it runs.
    """

    def __init__(self, db_path: Path) -> None:
        self.db_path = db_path
        self._lock = threading.Lock()
        self._proc: subprocess.Popen | None = None
        self._log: deque[str] = deque(maxlen=400)
        self.running = False
        self.started_at = 0.0
        self.finished_at = 0.0
        self.returncode: int | None = None
        self.pending_at_start = 0

    def _pending(self) -> int:
        db = Database(self.db_path)
        with db.connect() as connection:
            return connection.execute(
                "SELECT COUNT(*) FROM strings WHERE status IN ('new', 'stale')"
            ).fetchone()[0]

    def start(self, provider: str, limit: int | None, model: str | None) -> bool:
        with self._lock:
            if self.running:
                return False
            self.running = True
            self.returncode = None
            self.finished_at = 0.0
            self.started_at = time.time()
            self._log.clear()
            self.pending_at_start = self._pending()

        cmd = [sys.executable, str(TRANSLATE), "--db", str(self.db_path), "--provider", provider]
        if limit:
            cmd += ["--limit", str(limit)]
        if model:
            cmd += ["--model", model]

        self._log.append(f"$ {' '.join(cmd[1:])}")
        threading.Thread(target=self._run, args=(cmd,), daemon=True).start()
        return True

    def _run(self, cmd: list[str]) -> None:
        try:
            self._proc = subprocess.Popen(
                cmd,
                cwd=str(HERE),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            for line in self._proc.stdout:  # type: ignore[union-attr]
                self._log.append(line.rstrip())
            self.returncode = self._proc.wait()
        except Exception as error:  # noqa: BLE001 - surface any launch failure to the page
            self._log.append(f"panel: failed to run translate.py: {error}")
            self.returncode = -1
        finally:
            self.running = False
            self.finished_at = time.time()
            self._proc = None

    def stop(self) -> None:
        proc = self._proc
        if proc and proc.poll() is None:
            proc.terminate()
            self._log.append("panel: stop requested")

    def snapshot(self, pending_now: int) -> dict:
        done = max(0, self.pending_at_start - pending_now)
        total = self.pending_at_start
        return {
            "running": self.running,
            "returncode": self.returncode,
            "pendingAtStart": total,
            "pendingNow": pending_now,
            "done": done,
            "progress": (done / total) if total else (0.0 if self.running else 1.0),
            "elapsed": (self.finished_at or time.time()) - self.started_at if self.started_at else 0.0,
            "log": list(self._log),
        }


class CommandJob:
    """Runs a sequence of subprocesses (extract, then diff), stopping at the first failure.

    Used by the patch-update flow, where 'phase' matters more than a percentage: re-decoding the
    pack takes about a minute and either finishes or not.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._proc: subprocess.Popen | None = None
        self._log: deque[str] = deque(maxlen=400)
        self.running = False
        self.returncode: int | None = None
        self.phase = ""
        self.summary: dict[str, int] = {}

    def start(self, steps: list[tuple[str, list[str]]]) -> bool:
        with self._lock:
            if self.running:
                return False
            self.running = True
            self.returncode = None
            self.phase = ""
            self.summary = {}
            self._log.clear()
        threading.Thread(target=self._run, args=(steps,), daemon=True).start()
        return True

    def _parse_summary(self, line: str) -> None:
        # "Diff against the base: new 12, changed 3, removed 1, unchanged 106810"
        parts = line.split(":", 1)[1].replace(",", "").split() if ":" in line else []
        it = iter(parts)
        for name in it:
            try:
                self.summary[name] = int(next(it))
            except (StopIteration, ValueError):
                break

    def _run(self, steps: list[tuple[str, list[str]]]) -> None:
        try:
            for label, cmd in steps:
                self.phase = label
                self._log.append(f"$ [{label}] {Path(cmd[1]).name} {' '.join(cmd[2:])}")
                self._proc = subprocess.Popen(
                    cmd, cwd=str(HERE), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                    text=True, encoding="utf-8", errors="replace", bufsize=1,
                )
                for line in self._proc.stdout:  # type: ignore[union-attr]
                    line = line.rstrip()
                    self._log.append(line)
                    if line.startswith("Diff against the base:"):
                        self._parse_summary(line)
                rc = self._proc.wait()
                if rc != 0:
                    self.returncode = rc
                    self._log.append(f"[{label}] завершено с кодом {rc}")
                    return
            self.returncode = 0
        except Exception as error:  # noqa: BLE001
            self._log.append(f"panel: {error}")
            self.returncode = -1
        finally:
            self.running = False
            self.phase = ""
            self._proc = None

    def snapshot(self) -> dict:
        return {
            "running": self.running,
            "returncode": self.returncode,
            "phase": self.phase,
            "summary": self.summary,
            "log": list(self._log),
        }


# --------------------------------------------------------------------------- server

class PanelHandler(BaseHTTPRequestHandler):
    database: Database
    db_path: Path
    job: TranslationJob
    update_job: CommandJob

    def log_message(self, fmt: str, *args) -> None:  # noqa: A002 - stdlib signature
        pass

    # --- helpers ---------------------------------------------------------------

    def _send_json(self, payload: dict, status: int = 200) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _read_json(self) -> dict:
        length = int(self.headers.get("Content-Length", "0"))
        if not length:
            return {}
        return json.loads(self.rfile.read(length).decode("utf-8"))

    def _counts(self, connection) -> dict:
        rows = connection.execute("SELECT status, COUNT(*) FROM strings GROUP BY status").fetchall()
        by_status = {status: count for status, count in rows}
        total = sum(by_status.values())
        translated = by_status.get("mt", 0) + by_status.get("reviewed", 0) + by_status.get("locked", 0)
        pending = by_status.get("new", 0) + by_status.get("stale", 0)
        return {
            "total": total,
            "byStatus": by_status,
            "translated": translated,
            "pending": pending,
            "reviewQueue": by_status.get("mt", 0),
            "coverage": (translated / total) if total else 0.0,
        }

    # --- routing ---------------------------------------------------------------

    def do_GET(self) -> None:
        route = urlparse(self.path)
        path = route.path

        if path in ("/", "/index.html"):
            body = PAGE.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if path == "/api/status":
            env = read_env()
            with self.database.connect() as connection:
                counts = self._counts(connection)
            self._send_json(
                {
                    "db": counts,
                    "providers": {p: bool(env.get(var)) for p, var in KEY_ENV.items()},
                    "jobRunning": self.job.running,
                }
            )
            return

        if path == "/api/job":
            with self.database.connect() as connection:
                pending_now = connection.execute(
                    "SELECT COUNT(*) FROM strings WHERE status IN ('new', 'stale')"
                ).fetchone()[0]
            self._send_json(self.job.snapshot(pending_now))
            return

        if path == "/api/review":
            offset = int(parse_qs(route.query).get("offset", ["0"])[0])
            self._send_json(self._review_page(offset))
            return

        if path == "/api/update/job":
            snap = self.update_job.snapshot()
            snap["defaultPack"] = DEFAULT_PACK
            snap["extractorAvailable"] = EXTRACT.exists()
            self._send_json(snap)
            return

        self.send_error(404)

    def do_POST(self) -> None:
        path = urlparse(self.path).path
        try:
            payload = self._read_json()
        except (ValueError, json.JSONDecodeError) as error:
            self._send_json({"error": str(error)}, 400)
            return

        if path == "/api/key":
            provider = payload.get("provider", "anthropic")
            key = str(payload.get("key", "")).strip()
            if provider not in KEY_ENV:
                self._send_json({"error": f"unknown provider {provider}"}, 400)
                return
            if not key:
                self._send_json({"error": "empty key"}, 400)
                return
            write_env_key(KEY_ENV[provider], key)
            self._send_json({"ok": True, "provider": provider})
            return

        if path == "/api/translate":
            provider = payload.get("provider", "anthropic")
            if provider not in KEY_ENV:
                self._send_json({"error": f"unknown provider {provider}"}, 400)
                return
            if not read_env().get(KEY_ENV[provider]):
                self._send_json({"error": f"no key set for {provider}"}, 400)
                return
            limit = payload.get("limit")
            model = payload.get("model") or None
            if not self.job.start(provider, int(limit) if limit else None, model):
                self._send_json({"error": "a job is already running"}, 409)
                return
            self._send_json({"ok": True})
            return

        if path == "/api/job/stop":
            self.job.stop()
            self._send_json({"ok": True})
            return

        if path in ("/api/update/check", "/api/update/apply"):
            if not EXTRACT.exists():
                self._send_json({"error": f"extractor not found at {EXTRACT}"}, 400)
                return
            pack = str(payload.get("packPath") or DEFAULT_PACK)
            if not Path(pack).is_file():
                self._send_json({"error": f"data.pack not found: {pack}"}, 400)
                return
            extract_step = ("Извлечение", [sys.executable, str(EXTRACT), "--pack", pack, "--out", str(PAIRS_OUT), "--lang", "en"])
            if path == "/api/update/check":
                diff_step = ("Сравнение", [sys.executable, str(DIFF), "--pairs", str(PAIRS_OUT), "--db", str(self.db_path), "--dry-run"])
            else:
                diff_step = ("Применение", [sys.executable, str(DIFF), "--pairs", str(PAIRS_OUT), "--db", str(self.db_path), "--pack", pack, "--note", "panel update"])
            if not self.update_job.start([extract_step, diff_step]):
                self._send_json({"error": "an update is already running"}, 409)
                return
            self._send_json({"ok": True})
            return

        if path == "/api/review/save":
            try:
                string_id = int(payload["id"])
                russian = str(payload["ru"])
                status = payload.get("status", "reviewed")
            except (KeyError, ValueError) as error:
                self._send_json({"error": str(error)}, 400)
                return
            if status not in ("reviewed", "locked"):
                self._send_json({"error": f"unexpected status {status}"}, 400)
                return
            with self.database.connect() as connection:
                self.database.set_translation(connection, string_id, russian, status)
            self._send_json({"ok": True})
            return

        self.send_error(404)

    # --- review ----------------------------------------------------------------

    def _review_page(self, offset: int) -> dict:
        with self.database.connect() as connection:
            total = connection.execute("SELECT COUNT(*) FROM strings WHERE status = 'mt'").fetchone()[0]
            rows = connection.execute(
                "SELECT id, key, en, ru FROM strings WHERE status = 'mt' ORDER BY id LIMIT ? OFFSET ?",
                (REVIEW_PAGE_SIZE, offset),
            ).fetchall()

        items = []
        for row in rows:
            findings = validate(row["en"], row["ru"])
            items.append(
                {
                    "id": row["id"],
                    "key": row["key"] or "",
                    "en": row["en"],
                    "ru": row["ru"] or "",
                    "problems": [f"{f.problem.value}: {f.detail}" for f in findings],
                }
            )
        return {"total": total, "offset": offset, "pageSize": REVIEW_PAGE_SIZE, "items": items}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--port", type=int, default=8777)
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"{args.db} does not exist — run import_pairs.py first.", file=sys.stderr)
        return 1

    PanelHandler.database = Database(args.db)
    PanelHandler.db_path = args.db
    PanelHandler.job = TranslationJob(args.db)
    PanelHandler.update_job = CommandJob()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), PanelHandler)
    url = f"http://127.0.0.1:{args.port}"
    print(f"CZN Translator panel on {url} — Ctrl+C to stop.")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
