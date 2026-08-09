"""Offline conveyor for the CZN overlay translator (TZ §8).

Everything here runs by hand, with the game closed. Nothing in this package touches a running
game process — the only game file it ever reads is ``data.pack``, read-only, for its MD5.

Submodules are imported explicitly (``from czn.db import ...``) rather than eagerly here, so a
consumer that only needs the stdlib-only masking (``czn.segment``) and station transport
(``czn.station``) does not drag in ``czn.normalize`` and its xxhash dependency. That is what lets
the bundled embeddable Python run ``station_fill.py`` with no pip packages installed.
"""
