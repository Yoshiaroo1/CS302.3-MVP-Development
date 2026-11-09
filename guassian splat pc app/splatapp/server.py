"""Local HTTP API + static UI, served to the desktop window."""
import time
from pathlib import Path

from flask import Flask, abort, jsonify, request, send_file, send_from_directory

from . import STATIC_DIR, audit, jobs, nsenv, store

app = Flask(__name__, static_folder=None)
app.config["MAX_CONTENT_LENGTH"] = 8 * 1024 ** 3  # 8 GB uploads

# form field -> saved filename
UPLOAD_FIELDS = {
    "video": ("video.mp4", True),
    "poses": ("poses.jsonl", True),
    "intrinsics": ("intrinsics.json", False),
    "imu": ("imu.json", False),
}


# endpoint -> audit action. Static files and the once-a-second /api/state poll aren't interactions.
AUDITED = {
    "index": "app_page_opened",
    "upload": "scan_uploaded",
    "train": "scan_started",
    "stop": "job_stopped",
    "export": "export_started",
    "mesh": "export_downloaded",
    "log": "log_viewed",
    "delete": "session_deleted",
}
_LOG_REOPEN_GAP = 5.0       # the page polls an open log every 1.5 s; only record it being opened
_log_polls: dict = {}


def _public(s: dict) -> dict:
    keep = ("id", "status", "message", "progress", "frames", "frames_total", "created",
            "viewer_url", "eta", "pose_offset")
    out = {k: s.get(k) for k in keep}
    out["has_mesh"] = bool(s.get("mesh_zip") and Path(s["mesh_zip"]).exists())
    out["export_kind"] = "mesh" if (s.get("config") or "").replace("\\", "/").count("/nerfacto/") else "splat"
    return out


@app.after_request
def audit_request(response):
    try:
        _audit(response)
    except Exception as e:  # noqa: BLE001 — e.g. re-reading an oversized upload's form raises here
        audit.record("audit_error", endpoint=request.endpoint, http_status=response.status_code, error=str(e))
    return response


def _audit(response):
    action = AUDITED.get(request.endpoint)
    if request.endpoint == "state" and request.args.get("refresh") == "1":
        action = "nerfstudio_rechecked"
    if not action:
        return
    sid = (request.view_args or {}).get("sid")
    if action == "log_viewed":
        now, last = time.monotonic(), _log_polls.get(sid, 0.0)
        _log_polls[sid] = now
        if now - last < _LOG_REOPEN_GAP:
            return
    details = {"http_status": response.status_code}
    body = response.get_json(silent=True) if response.is_json else None
    if isinstance(body, dict):
        sid = sid or body.get("id")
        if body.get("error"):
            details["error"] = body["error"]
    if action == "scan_uploaded":
        details["files"] = {k: f.filename for k, f in request.files.items() if f.filename}
        details["request_bytes"] = request.content_length
    audit.record(action, sid, **details)


@app.get("/")
def index():
    return send_from_directory(STATIC_DIR, "index.html")


@app.get("/static/<path:name>")
def static_files(name):
    return send_from_directory(STATIC_DIR, name)


@app.get("/api/state")
def state():
    env = nsenv.find(refresh=request.args.get("refresh") == "1")
    return jsonify({
        "nerfstudio": {"found": bool(env), "path": str(env["bin_dir"]) if env else None},
        "sessions": [_public(s) for s in store.all_sessions()],
    })


@app.post("/api/upload")
def upload():
    missing = [f for f, (_, req) in UPLOAD_FIELDS.items() if req and not request.files.get(f)]
    if missing:
        return jsonify(error=f"Missing required file(s): {', '.join(missing)}"), 400
    s = store.create()
    raw = store.session_dir(s["id"]) / "raw"
    raw.mkdir(parents=True, exist_ok=True)
    for field, (fname, _) in UPLOAD_FIELDS.items():
        f = request.files.get(field)
        if f and f.filename:
            f.save(raw / fname)
    jobs.start_prepare(s["id"])
    return jsonify(_public(store.get(s["id"])))


def _session_or_404(sid):
    s = store.get(sid)
    if not s:
        abort(404)
    return s


@app.post("/api/sessions/<sid>/train")
def train(sid):
    s = _session_or_404(sid)
    if s["status"] not in ("ready", "failed", "complete") or not s.get("frames"):
        return jsonify(error=f"Can't train a session that is {s['status']}."), 409
    if not nsenv.find(refresh=True):
        return jsonify(error="NerfStudio isn't installed yet — run setup_nerfstudio.ps1 first."), 409
    jobs.enqueue_train(sid)
    return jsonify(_public(store.get(sid)))


@app.post("/api/sessions/<sid>/stop")
def stop(sid):
    _session_or_404(sid)
    jobs.stop(sid)
    s = store.get(sid)
    if s["status"] == "queued":
        store.update(sid, status="ready", message="Removed from queue.")
    elif s["status"] == "exporting":
        store.update(sid, status="complete", progress=None, message="Export cancelled.")
    return jsonify(_public(store.get(sid)))


@app.post("/api/sessions/<sid>/export")
def export(sid):
    s = _session_or_404(sid)
    if s["status"] != "complete":
        return jsonify(error="Only finished scans can be exported."), 409
    jobs.enqueue_export(sid)
    return jsonify(_public(store.get(sid)))


@app.get("/api/sessions/<sid>/mesh")
def mesh(sid):
    s = _session_or_404(sid)
    if not s.get("mesh_zip") or not Path(s["mesh_zip"]).exists():
        abort(404)
    return send_file(s["mesh_zip"], as_attachment=True, download_name=Path(s["mesh_zip"]).name)


@app.get("/api/sessions/<sid>/log")
def log(sid):
    _session_or_404(sid)
    return jsonify(log=store.read_log(sid))


@app.delete("/api/sessions/<sid>")
def delete(sid):
    _session_or_404(sid)
    jobs.stop(sid)
    store.delete(sid)
    return jsonify(ok=True)
