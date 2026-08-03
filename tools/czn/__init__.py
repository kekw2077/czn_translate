"""Offline conveyor for the CZN overlay translator (TZ §8).

Everything here runs by hand, with the game closed. Nothing in this package touches a running
game process — the only game file it ever reads is ``data.pack``, read-only, for its MD5.
"""

from . import db, normalize, ollama, tables, validate

__all__ = ["db", "normalize", "ollama", "tables", "validate"]
