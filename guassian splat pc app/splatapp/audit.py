"""Append-only, hash-chained record of interactions and changes (SQLite, <APP_DATA>/audit.db).

Each row's SHA-256 covers its own fields plus the previous row's hash, so editing, reordering or
removing a row breaks the chain from that point on. Triggers reject UPDATE/DELETE. Details stay
readable; the hash proves integrity, it doesn't hide the content. Logging never breaks the app:
a failed write is reported on stderr and the action carries on.

    python -m splatapp.audit verify        # check the whole chain (exit code 1 if broken)
    python -m splatapp.audit list [N]      # print the last N entries (default 50)
"""
import getpass
import hashlib
import json
import sqlite3
import sys
import threading
from datetime import datetime
from pathlib import Path

from . import APP_DATA, ROOT, STATIC_DIR

DB_PATH = APP_DATA / "audit.db"
GENESIS = "0" * 64

_SCHEMA = """
CREATE TABLE IF NOT EXISTS audit_log (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    ts         TEXT NOT NULL,
    actor      TEXT NOT NULL,
    action     TEXT NOT NULL,
    session_id TEXT,
    details    TEXT NOT NULL,
    prev_hash  TEXT NOT NULL,
    hash       TEXT NOT NULL UNIQUE
);
CREATE INDEX IF NOT EXISTS audit_log_session ON audit_log(session_id);
CREATE INDEX IF NOT EXISTS audit_log_action ON audit_log(action);
CREATE TRIGGER IF NOT EXISTS audit_log_no_update BEFORE UPDATE ON audit_log
    BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER IF NOT EXISTS audit_log_no_delete BEFORE DELETE ON audit_log
    BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
"""

_lock = threading.Lock()
_settings_lock = threading.Lock()
_ready = False
_settings_seen = None   # last settings dict recorded, cached so settings.load() stays cheap


def _actor() -> str:
    try:
        return getpass.getuser()
    except Exception:  # noqa: BLE001 — getuser can raise several types when no user is set
        return "unknown"


def _connect() -> sqlite3.Connection:
    global _ready
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    # isolation_level=None: transactions are explicit, so reading the chain tip and writing the
    # next row happen under one BEGIN IMMEDIATE lock.
    conn = sqlite3.connect(DB_PATH, timeout=30, isolation_level=None)
    if not _ready:
        conn.execute("PRAGMA journal_mode=WAL")
        conn.executescript(_SCHEMA)
        _ready = True
    return conn


def _entry_hash(ts, actor, action, session_id, details, prev_hash) -> str:
    payload = json.dumps([ts, actor, action, session_id, details, prev_hash],
                         ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def record(action: str, session_id: str = None, **details):
    """Append one entry. Returns its hash, or None if it couldn't be written."""
    try:
        ts = datetime.now().astimezone().isoformat(timespec="milliseconds")
        actor = _actor()
        body = json.dumps(details, ensure_ascii=False, sort_keys=True, default=str)
        with _lock:
            conn = _connect()
            try:
                conn.execute("BEGIN IMMEDIATE")
                row = conn.execute("SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1").fetchone()
                prev = row[0] if row else GENESIS
                h = _entry_hash(ts, actor, action, session_id, body, prev)
                conn.execute("INSERT INTO audit_log (ts, actor, action, session_id, details, prev_hash, hash) "
                             "VALUES (?, ?, ?, ?, ?, ?, ?)", (ts, actor, action, session_id, body, prev, h))
                conn.execute("COMMIT")
            except Exception:
                if conn.in_transaction:
                    conn.execute("ROLLBACK")
                raise
            finally:
                conn.close()
        return h
    except Exception as e:  # noqa: BLE001 — the record must never take the app down
        print(f"[audit] could not record {action}: {e}", file=sys.stderr)
        return None


def verify() -> dict:
    """Walk the whole chain. Returns dict(ok, entries, bad_id, reason)."""
    n = 0
    try:
        conn = _connect()
    except sqlite3.Error as e:
        return {"ok": False, "entries": 0, "bad_id": None, "reason": f"database unreadable: {e}"}
    try:
        prev, expected_id = GENESIS, None
        for rid, ts, actor, action, sid, details, prev_hash, h in conn.execute(
                "SELECT id, ts, actor, action, session_id, details, prev_hash, hash FROM audit_log ORDER BY id"):
            if expected_id is not None and rid != expected_id:
                return {"ok": False, "entries": n, "bad_id": rid, "reason": f"entries before id {rid} are missing"}
            if prev_hash != prev:
                return {"ok": False, "entries": n, "bad_id": rid, "reason": "previous-hash link doesn't match"}
            if _entry_hash(ts, actor, action, sid, details, prev_hash) != h:
                return {"ok": False, "entries": n, "bad_id": rid, "reason": "entry contents don't match its hash"}
            prev, expected_id = h, rid + 1
            n += 1
        return {"ok": True, "entries": n, "bad_id": None, "reason": None}
    except sqlite3.Error as e:
        return {"ok": False, "entries": n, "bad_id": None, "reason": f"database unreadable: {e}"}
    finally:
        conn.close()


def file_digest(path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def files_summary(paths) -> dict:
    """{name: {bytes, sha256}} for the given files; an unreadable file records its error instead."""
    out = {}
    for p in map(Path, paths):
        try:
            out[p.name] = {"bytes": p.stat().st_size, "sha256": file_digest(p)}
        except OSError as e:
            out[p.name] = {"error": str(e)}
    return out


def _latest_details(*actions):
    conn = _connect()
    try:
        marks = ",".join("?" * len(actions))
        row = conn.execute(f"SELECT details FROM audit_log WHERE action IN ({marks}) ORDER BY id DESC LIMIT 1",
                           actions).fetchone()
        return json.loads(row[0]) if row else None
    finally:
        conn.close()


def _code_files():
    files = [ROOT / "app.py", ROOT / "requirements.txt", ROOT / "setup_nerfstudio.ps1"]
    files += sorted((ROOT / "splatapp").glob("*.py")) + sorted(p for p in STATIC_DIR.rglob("*") if p.is_file())
    return [p for p in files if p.exists()]


def record_code_version():
    """Log the app's own files at startup, and which ones changed since the last run."""
    try:
        now = {p.relative_to(ROOT).as_posix(): file_digest(p) for p in _code_files()}
        last = _latest_details("code_recorded", "code_changed")
        if last is None:
            record("code_recorded", files=now)
        elif last["files"] != now:
            old = last["files"]
            record("code_changed", files=now,
                   added=sorted(set(now) - set(old)), removed=sorted(set(old) - set(now)),
                   modified=sorted(k for k in set(now) & set(old) if now[k] != old[k]))
    except Exception as e:  # noqa: BLE001
        print(f"[audit] could not record code version: {e}", file=sys.stderr)


def record_settings(current: dict):
    """Called whenever settings are loaded; logs only when they differ from the last record."""
    global _settings_seen
    try:
        with _settings_lock:
            if current == _settings_seen:
                return
            last = _settings_seen
            if last is None:
                stored = _latest_details("settings_recorded", "settings_changed")
                last = stored["settings"] if stored else None
            if last is None:
                record("settings_recorded", settings=current)
            elif last != current:
                changed = {k: [last.get(k), current.get(k)] for k in sorted(set(last) | set(current))
                           if last.get(k) != current.get(k)}
                record("settings_changed", settings=current, changed=changed)
            _settings_seen = dict(current)
    except Exception as e:  # noqa: BLE001
        print(f"[audit] could not record settings: {e}", file=sys.stderr)


def _main(argv) -> int:
    cmd = argv[0] if argv else "verify"
    if cmd == "verify":
        r = verify()
        if r["ok"]:
            print(f"OK: {r['entries']} entries, chain intact ({DB_PATH}).")
            return 0
        print(f"BROKEN at id {r['bad_id']}: {r['reason']} ({r['entries']} entries checked first).")
        return 1
    if cmd == "list":
        limit = int(argv[1]) if len(argv) > 1 else 50
        conn = _connect()
        rows = conn.execute("SELECT id, ts, actor, action, session_id, details, hash FROM audit_log "
                            "ORDER BY id DESC LIMIT ?", (limit,)).fetchall()
        conn.close()
        for rid, ts, actor, action, sid, details, h in reversed(rows):
            print(f"{rid:>6}  {ts}  {actor}  {action:<20} {sid or '-':<17} {h[:12]}  {details[:160]}")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
