"""Local HTTP API + static UI, served to the desktop window."""
from pathlib import Path

from flask import Flask, abort, jsonify, request, send_file, send_from_directory

from . import STATIC_DIR, jobs, nsenv, store

app = Flask(__name__, static_folder=None)
app.config["MAX_CONTENT_LENGTH"] = 8 * 1024 ** 3  # 8 GB uploads

# form field -> saved filename
UPLOAD_FIELDS = {
    "video": ("video.mp4", True),
    "poses": ("poses.jsonl", True),
    "intrinsics": ("intrinsics.json", False),
    "imu": ("imu.json", False),
}


def _public(s: dict) -> dict:
    keep = ("id", "status", "message", "progress", "frames", "frames_total", "created",
            "viewer_url", "eta", "pose_offset")
    out = {k: s.get(k) for k in keep}
    out["has_mesh"] = bool(s.get("mesh_zip") and Path(s["mesh_zip"]).exists())
    out["export_kind"] = "mesh" if (s.get("config") or "").replace("\\", "/").count("/nerfacto/") else "splat"
    return out


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
