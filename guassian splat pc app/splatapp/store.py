"""Session persistence: one folder + session.json per capture."""
import json
import secrets
import shutil
import string
import threading
from datetime import datetime
from pathlib import Path

from . import DATA_DIR

_lock = threading.RLock()
_sessions: dict = {}

# Statuses shown in the SESSIONS table vs the FINISHED table.
ACTIVE = {"uploading", "preparing", "ready", "queued", "running", "failed"}
FINISHED = {"complete", "exporting"}

# Statuses that need a live process; after an app restart they can't be resumed.
_INTERRUPTED = {
    "preparing": ("failed", "App closed while preparing — delete and re-upload."),
    "uploading": ("failed", "Upload interrupted — delete and re-upload."),
    "queued": ("ready", "Training was queued when the app closed — press play to start."),
    "running": ("ready", "Training was interrupted when the app closed — press play to restart."),
    "exporting": ("complete", "Export was interrupted — press download to retry."),
}

_ALPHABET = string.ascii_letters + string.digits


def session_dir(sid: str) -> Path:
    return DATA_DIR / sid


def _save(s: dict):
    d = session_dir(s["id"])
    d.mkdir(parents=True, exist_ok=True)
    tmp = d / "session.json.tmp"
    tmp.write_text(json.dumps(s, indent=2), encoding="utf-8")
    tmp.replace(d / "session.json")


def load_all():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    with _lock:
        for f in DATA_DIR.glob("*/session.json"):
            try:
                s = json.loads(f.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if s.get("status") in _INTERRUPTED:
                s["status"], s["message"] = _INTERRUPTED[s["status"]]
                s["progress"] = None
                _save(s)
            _sessions[s["id"]] = s


def create() -> dict:
    with _lock:
        sid = "".join(secrets.choice(_ALPHABET) for _ in range(16))
        s = {
            "id": sid,
            "status": "uploading",
            "message": "Uploading…",
            "progress": None,
            "frames": None,
            "frames_total": None,
            "created": datetime.now().astimezone().isoformat(timespec="seconds"),
            "viewer_url": None,
            "eta": None,
            "config": None,
            "mesh_zip": None,
        }
        _sessions[sid] = s
        _save(s)
        return dict(s)


def get(sid: str):
    with _lock:
        s = _sessions.get(sid)
        return dict(s) if s else None


def update(sid: str, **fields):
    with _lock:
        s = _sessions.get(sid)
        if not s:
            return None
        s.update(fields)
        _save(s)
        return dict(s)


def all_sessions():
    with _lock:
        return sorted((dict(s) for s in _sessions.values()), key=lambda s: s["created"])


def delete(sid: str):
    with _lock:
        _sessions.pop(sid, None)
    shutil.rmtree(session_dir(sid), ignore_errors=True)


def log_path(sid: str) -> Path:
    return session_dir(sid) / "log.txt"


def append_log(sid: str, line: str):
    try:
        with open(log_path(sid), "a", encoding="utf-8") as f:
            f.write(line.rstrip("\n") + "\n")
    except OSError:
        pass


def read_log(sid: str, max_lines: int = 400) -> str:
    p = log_path(sid)
    if not p.exists():
        return ""
    lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
    return "\n".join(lines[-max_lines:])
