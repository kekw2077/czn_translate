"""Working out what a game file actually is.

The conveyor in §8 assumes an AssetRipper JSON export, which only helps if the strings live in
Unity assets. Plenty of Korean gacha titles keep their master data in a SQLite database instead —
sometimes as a loose file, sometimes concatenated into a container like ``data.pack``. This
module answers "what am I looking at" before anything tries to parse it.

Everything here is read-only and never touches a running game (§0). It is the same class of
access §7 already sanctions for the ``data.pack`` MD5 check.
"""

from __future__ import annotations

import math
import mmap
import struct
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

SQLITE_MAGIC = b"SQLite format 3\x00"

# Header layout of a SQLite database, as documented in the file format spec.
_SQLITE_PAGE_SIZE_OFFSET = 16
_SQLITE_WRITE_VERSION_OFFSET = 18
_SQLITE_READ_VERSION_OFFSET = 19
_SQLITE_PAGE_COUNT_OFFSET = 28
_SQLITE_HEADER_SIZE = 100

_MAGICS: list[tuple[bytes, str, str]] = [
    (SQLITE_MAGIC, "sqlite3", "SQLite database"),
    (b"UnityFS", "unityfs", "Unity asset bundle (UnityFS)"),
    (b"UnityWeb", "unityweb", "Unity asset bundle (legacy UnityWeb)"),
    (b"UnityRaw", "unityraw", "Unity asset bundle (legacy UnityRaw)"),
    (b"PK\x03\x04", "zip", "ZIP archive"),
    (b"\x1f\x8b", "gzip", "gzip stream"),
    (b"\x04\x22\x4d\x18", "lz4", "LZ4 frame"),
    (b"\x28\xb5\x2f\xfd", "zstd", "Zstandard stream"),
    (b"BZh", "bzip2", "bzip2 stream"),
    (b"\x37\x7a\xbc\xaf\x27\x1c", "7z", "7-Zip archive"),
    (b"Rar!\x1a\x07", "rar", "RAR archive"),
    (b"\x89PNG\r\n\x1a\n", "png", "PNG image"),
    (b"OggS", "ogg", "Ogg container"),
    (b"RIFF", "riff", "RIFF container (WAV/AVI)"),
]

# Above this a byte histogram is indistinguishable from noise: compressed or encrypted.
HIGH_ENTROPY = 7.5

READ_WINDOW = 1 << 20


@dataclass(frozen=True)
class FormatGuess:
    kind: str
    description: str
    entropy: float

    @property
    def is_container(self) -> bool:
        return self.kind in {"zip", "7z", "rar", "unityfs", "unityweb", "unityraw"}

    @property
    def is_readable_now(self) -> bool:
        """True when the conveyor can consume it as-is, with no unpacking step."""
        return self.kind in {"sqlite3", "json", "csv", "xml", "text"}


def shannon_entropy(data: bytes) -> float:
    """Bits per byte. 8.0 is uniform noise, English prose sits near 4."""
    if not data:
        return 0.0

    counts = Counter(data)
    total = len(data)
    return -sum((n / total) * math.log2(n / total) for n in counts.values())


def _looks_like_text(sample: bytes) -> bool:
    if b"\x00" in sample[:512]:
        return False

    try:
        sample.decode("utf-8")
    except UnicodeDecodeError:
        return False

    printable = sum(1 for byte in sample if byte in (9, 10, 13) or 32 <= byte < 127 or byte >= 128)
    return printable / len(sample) > 0.95


def _classify_text(sample: bytes) -> tuple[str, str]:
    stripped = sample.lstrip()
    if stripped[:1] in (b"{", b"["):
        return "json", "JSON text"
    if stripped[:1] == b"<":
        return "xml", "XML/HTML text"

    first_line = stripped.split(b"\n", 1)[0]
    if first_line.count(b",") >= 2 or first_line.count(b"\t") >= 2:
        return "csv", "delimited text (CSV/TSV)"

    return "text", "plain text"


def identify(data: bytes) -> FormatGuess:
    """Best guess for a blob, from its leading bytes and its byte histogram."""
    if not data:
        return FormatGuess("empty", "empty file", 0.0)

    entropy = shannon_entropy(data[:READ_WINDOW])

    for magic, kind, description in _MAGICS:
        if data.startswith(magic):
            return FormatGuess(kind, description, entropy)

    sample = data[:8192]
    if _looks_like_text(sample):
        kind, description = _classify_text(sample)
        return FormatGuess(kind, description, entropy)

    if entropy >= HIGH_ENTROPY:
        return FormatGuess(
            "opaque",
            "high-entropy binary — compressed, encrypted, or both",
            entropy,
        )

    return FormatGuess("binary", "unrecognized binary", entropy)


def identify_file(path: Path, window: int = READ_WINDOW) -> FormatGuess:
    """Reads at most ``window`` bytes. A directory sweep should pass something small — the
    entropy figure gets rougher, but the magic check, which is what a sweep is after, does not."""
    with path.open("rb") as handle:
        return identify(handle.read(window))


@dataclass(frozen=True)
class EmbeddedDatabase:
    offset: int
    size: int
    page_size: int
    page_count: int


def _validate_sqlite_header(header: bytes) -> tuple[int, int] | None:
    """Returns ``(page_size, page_count)`` when the header is genuinely a SQLite one.

    The magic string alone is a false-positive magnet — sixteen bytes of ASCII will turn up
    inside compressed data and inside any file that merely mentions SQLite. Checking the fields
    behind it is what makes carving a container usable rather than a source of junk.
    """
    if len(header) < _SQLITE_HEADER_SIZE:
        return None

    (raw_page_size,) = struct.unpack_from(">H", header, _SQLITE_PAGE_SIZE_OFFSET)

    # 1 is the spec's escape for 65536; everything else must be a power of two in 512..32768.
    if raw_page_size == 1:
        page_size = 65536
    elif 512 <= raw_page_size <= 32768 and (raw_page_size & (raw_page_size - 1)) == 0:
        page_size = raw_page_size
    else:
        return None

    write_version = header[_SQLITE_WRITE_VERSION_OFFSET]
    read_version = header[_SQLITE_READ_VERSION_OFFSET]
    if write_version not in (1, 2) or read_version not in (1, 2):
        return None

    (page_count,) = struct.unpack_from(">I", header, _SQLITE_PAGE_COUNT_OFFSET)
    if page_count == 0:
        return None

    return page_size, page_count


def find_embedded_sqlite(path: Path, limit: int = 16) -> list[EmbeddedDatabase]:
    """Locates SQLite databases inside a container blob.

    A ``data.pack`` that is a plain concatenation of assets will hold its databases verbatim, so
    scanning for the header and validating it recovers them without knowing the container format
    at all. That is worth trying before reverse-engineering the archive layout.
    """
    size = path.stat().st_size
    if size < _SQLITE_HEADER_SIZE:
        return []

    found: list[EmbeddedDatabase] = []

    with path.open("rb") as handle, mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ) as view:
        cursor = 0
        while len(found) < limit:
            offset = view.find(SQLITE_MAGIC, cursor)
            if offset < 0:
                break

            cursor = offset + 1

            header = view[offset:offset + _SQLITE_HEADER_SIZE]
            validated = _validate_sqlite_header(header)
            if validated is None:
                continue

            page_size, page_count = validated
            length = page_size * page_count

            # A page count that runs past the end of the container means the header was noise
            # that happened to validate, or the database is truncated. Either way it is not
            # something to hand to sqlite3.
            if offset + length > size:
                continue

            found.append(EmbeddedDatabase(offset, length, page_size, page_count))

            # Skip past this database: its own pages can contain the magic string again.
            cursor = offset + length

    return found


def extract_embedded(path: Path, database: EmbeddedDatabase, destination: Path) -> Path:
    """Copies one carved database out to its own file, leaving the source untouched."""
    destination.parent.mkdir(parents=True, exist_ok=True)

    with path.open("rb") as source, destination.open("wb") as target:
        source.seek(database.offset)
        remaining = database.size
        while remaining > 0:
            block = source.read(min(remaining, 1 << 20))
            if not block:
                break
            target.write(block)
            remaining -= len(block)

    return destination
