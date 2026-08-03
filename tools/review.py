#!/usr/bin/env python3
"""Local review UI for the machine-translated queue (TZ §8).

    python review.py --db ../czn.db

Serves on 127.0.0.1 only, with no dependencies beyond the standard library. Approving a string
moves it from 'mt' to 'reviewed', which is also what makes it reusable as translation memory.
"""

from __future__ import annotations

import argparse
import html
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from czn.db import STATUS_LOCKED, STATUS_MT, STATUS_REVIEWED, Database
from czn.validate import validate

PAGE_SIZE = 50

STYLE = """
body { font: 14px/1.5 system-ui, sans-serif; margin: 0; background: #14101a; color: #ece6f0; }
header { padding: 12px 20px; background: #1e1826; position: sticky; top: 0; border-bottom: 1px solid #322a3c; }
main { padding: 16px 20px 60px; max-width: 1100px; }
.row { border: 1px solid #322a3c; border-radius: 8px; padding: 12px; margin-bottom: 10px; background: #1a1522; }
.en { color: #b9adc4; margin-bottom: 6px; white-space: pre-wrap; }
.key { color: #6f6480; font-size: 12px; }
textarea { width: 100%; min-height: 48px; background: #120e18; color: #ece6f0;
           border: 1px solid #3d3348; border-radius: 6px; padding: 8px; font: inherit; box-sizing: border-box; }
button { background: #4a3a63; color: #f2ecf7; border: 0; border-radius: 6px; padding: 7px 14px;
         cursor: pointer; margin-right: 6px; }
button.lock { background: #33445e; }
.problem { color: #ffb3a7; font-size: 12px; margin-top: 6px; }
.empty { color: #8d8298; padding: 40px 0; }
nav a { color: #c4a7ff; margin-right: 12px; }
"""

SCRIPT = """
async function save(id, status) {
  const value = document.getElementById('ru-' + id).value;
  const response = await fetch('/save', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({id: id, ru: value, status: status})
  });
  if (response.ok) {
    document.getElementById('row-' + id).style.display = 'none';
  } else {
    alert('Save failed: ' + await response.text());
  }
}
"""


class ReviewHandler(BaseHTTPRequestHandler):
    database: Database

    def log_message(self, format: str, *args) -> None:  # noqa: A002 - stdlib signature
        pass

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path not in ("/", "/index.html"):
            self.send_error(404)
            return

        offset = int(parse_qs(parsed.query).get("offset", ["0"])[0])
        self._send_html(self._render(offset))

    def do_POST(self) -> None:
        if urlparse(self.path).path != "/save":
            self.send_error(404)
            return

        length = int(self.headers.get("Content-Length", "0"))
        try:
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            string_id = int(payload["id"])
            russian = str(payload["ru"])
            status = payload.get("status", STATUS_REVIEWED)
        except (ValueError, KeyError, json.JSONDecodeError) as error:
            self.send_error(400, str(error))
            return

        if status not in (STATUS_REVIEWED, STATUS_LOCKED):
            self.send_error(400, f"unexpected status {status}")
            return

        with self.database.connect() as connection:
            self.database.set_translation(connection, string_id, russian, status)

        self.send_response(204)
        self.end_headers()

    def _send_html(self, body: str) -> None:
        encoded = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def _render(self, offset: int) -> str:
        with self.database.connect() as connection:
            total = connection.execute(
                "SELECT COUNT(*) FROM strings WHERE status = ?", (STATUS_MT,)
            ).fetchone()[0]

            rows = connection.execute(
                """
                SELECT id, key, en, ru FROM strings
                WHERE status = ? ORDER BY id LIMIT ? OFFSET ?
                """,
                (STATUS_MT, PAGE_SIZE, offset),
            ).fetchall()

        parts = [
            "<!doctype html><html lang='ru'><head><meta charset='utf-8'>",
            "<title>CZN review</title>",
            f"<style>{STYLE}</style></head><body>",
            f"<header><strong>Очередь ревью</strong> — {total} строк со статусом mt</header><main>",
        ]

        if not rows:
            parts.append("<p class='empty'>Очередь пуста.</p>")

        for row in rows:
            findings = validate(row["en"], row["ru"])
            problems = "".join(
                f"<div class='problem'>{html.escape(f.problem.value)}: {html.escape(f.detail)}</div>"
                for f in findings
            )
            parts.append(
                f"<div class='row' id='row-{row['id']}'>"
                f"<div class='key'>#{row['id']} {html.escape(row['key'] or '(без ключа)')}</div>"
                f"<div class='en'>{html.escape(row['en'])}</div>"
                f"<textarea id='ru-{row['id']}'>{html.escape(row['ru'] or '')}</textarea>"
                f"{problems}"
                f"<div style='margin-top:8px'>"
                f"<button onclick='save({row['id']}, \"reviewed\")'>Принять</button>"
                f"<button class='lock' onclick='save({row['id']}, \"locked\")'>Принять и закрепить</button>"
                f"</div></div>"
            )

        parts.append("<nav>")
        if offset > 0:
            parts.append(f"<a href='/?offset={max(0, offset - PAGE_SIZE)}'>← назад</a>")
        if offset + PAGE_SIZE < total:
            parts.append(f"<a href='/?offset={offset + PAGE_SIZE}'>вперёд →</a>")
        parts.append("</nav>")

        parts.append(f"</main><script>{SCRIPT}</script></body></html>")
        return "".join(parts)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--port", type=int, default=8777)
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"{args.db} does not exist.", file=sys.stderr)
        return 1

    ReviewHandler.database = Database(args.db)

    # Loopback only. This UI has no authentication and edits the translation base directly.
    server = ThreadingHTTPServer(("127.0.0.1", args.port), ReviewHandler)
    print(f"Review UI on http://127.0.0.1:{args.port} — Ctrl+C to stop.")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        server.server_close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
