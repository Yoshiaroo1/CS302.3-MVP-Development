"""Background work: CPU dataset prep runs immediately; GPU jobs (train/export) run one at a time."""
import os
import queue
import re
import shutil
import subprocess
import threading
import traceback
import zipfile
from pathlib import Path

from . import nsenv, prepare, store
from . import settings as settings_mod

_gpu_queue: "queue.Queue[tuple[str, str]]" = queue.Queue()
_procs: dict = {}            # sid -> Popen of the running ns-* process
_cancelled: set = set()      # sids whose job was stopped/deleted
_lock = threading.Lock()

# "1200 (4.00%)    35.120 ms    17 m, 32 s    1.21 M"
_STEP_RE = re.compile(r"^\s*(\d+)\s+\((\d+(?:\.\d+)?)%\)\s+(.*)$")
_URL_RE = re.compile(r"https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0)(?::\d+)?[^\s\]\)│]*")
_PCT_RE = re.compile(r"(\d{1,3}(?:\.\d+)?)%")
_ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")
_BOX_RE = re.compile(r"[│┃║╭╮╰╯─━═┏┓┗┛┌┐└┘]+")

_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


# ---------------------------------------------------------------- prepare (CPU)

def start_prepare(sid: str):
    threading.Thread(target=_run_prepare, args=(sid,), daemon=True, name=f"prepare-{sid}").start()


def _run_prepare(sid: str):
    def log(msg):
        store.append_log(sid, msg)

    def progress(pct, msg):
        store.update(sid, progress=round(pct, 1), message=msg)

    store.update(sid, status="preparing", progress=0, message="Preparing dataset…")
    log("=== Preparing dataset ===")
    try:
        info = prepare.prepare(store.session_dir(sid), settings_mod.load(), log, progress)
        store.update(sid, status="ready", progress=None, message=f"{info['frames']} of {info['frames_total']} frames usable — press play to train.",
                     frames=info["frames"], frames_total=info["frames_total"],
                     pose_offset=info["pose_offset"], pose_error_deg=info["pose_error_deg"])
    except prepare.PrepareError as e:
        log(f"ERROR: {e}")
        store.update(sid, status="failed", progress=None, message=str(e))
    except Exception as e:  # noqa: BLE001 — surface anything to the UI
        log(traceback.format_exc())
        store.update(sid, status="failed", progress=None, message=f"Preparation failed: {e}")


# ---------------------------------------------------------------- GPU queue

def enqueue_train(sid: str):
    with _lock:
        _cancelled.discard(sid)
    store.update(sid, status="queued", progress=None, eta=None, message="Waiting for the GPU…")
    _gpu_queue.put(("train", sid))


def enqueue_export(sid: str):
    with _lock:
        _cancelled.discard(sid)
    store.update(sid, status="exporting", progress=None, eta=None, message="Waiting for the GPU…")
    _gpu_queue.put(("export", sid))


def stop(sid: str):
    """Stop a queued or running job for this session."""
    with _lock:
        _cancelled.add(sid)
        proc = _procs.get(sid)
    if proc and proc.poll() is None:
        _kill_tree(proc)


def _kill_tree(proc):
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                       capture_output=True, creationflags=_NO_WINDOW)
    else:
        proc.kill()


def shutdown():
    with _lock:
        procs = list(_procs.items())
        _cancelled.update(sid for sid, _ in procs)
    for _, p in procs:
        if p.poll() is None:
            _kill_tree(p)


def _worker():
    while True:
        kind, sid = _gpu_queue.get()
        try:
            with _lock:
                if sid in _cancelled:
                    continue
            if not store.get(sid):
                continue
            if kind == "train":
                _run_train(sid)
            else:
                _run_export(sid)
        except Exception as e:  # noqa: BLE001
            store.append_log(sid, traceback.format_exc())
            if kind == "train":
                store.update(sid, status="failed", progress=None, message=f"Training failed: {e}")
            else:
                store.update(sid, status="complete", progress=None, message=f"Mesh export failed: {e}")
        finally:
            _gpu_queue.task_done()


threading.Thread(target=_worker, daemon=True, name="gpu-worker").start()


def _clean(line: str) -> str:
    line = _ANSI_RE.sub("", line)
    return _BOX_RE.sub(" ", line).strip()


def _run_process(sid: str, args: list, on_line) -> int:
    store.append_log(sid, "$ " + subprocess.list2cmdline(args))
    proc = subprocess.Popen(
        args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
        env=nsenv.subprocess_env(), cwd=str(store.session_dir(sid)), creationflags=_NO_WINDOW)
    with _lock:
        _procs[sid] = proc
    try:
        buf = b""
        while True:
            chunk = proc.stdout.read1(4096) if hasattr(proc.stdout, "read1") else proc.stdout.read(4096)
            if not chunk:
                break
            buf += chunk
            # rich progress bars use \r; treat it as a line break too
            *lines, buf = re.split(rb"\r\n|\r|\n", buf)
            for raw in lines:
                line = _clean(raw.decode("utf-8", errors="replace"))
                if line:
                    store.append_log(sid, line)
                    on_line(line)
        if buf.strip():
            line = _clean(buf.decode("utf-8", errors="replace"))
            store.append_log(sid, line)
            on_line(line)
        return proc.wait()
    finally:
        with _lock:
            _procs.pop(sid, None)


def _was_cancelled(sid: str) -> bool:
    with _lock:
        return sid in _cancelled


# ---------------------------------------------------------------- training

def find_config(sid: str):
    configs = sorted((store.session_dir(sid) / "outputs").glob("**/config.yml"), key=lambda p: p.stat().st_mtime)
    return configs[-1] if configs else None


def _run_train(sid: str):
    cfg = settings_mod.load()
    sdir = store.session_dir(sid)
    out_dir = sdir / "outputs"
    shutil.rmtree(out_dir, ignore_errors=True)
    iters = int(cfg.get("max_iterations", 30000))
    port = int(cfg.get("viewer_port", 7007))

    store.update(sid, status="running", progress=0, eta=None, viewer_url=None, config=None, mesh_zip=None,
                 message="Starting NerfStudio (first launch can take a minute)…")
    store.append_log(sid, "=== Training nerfacto ===")
    args = [
        nsenv.tool("ns-train"), "nerfacto",
        "--data", str(sdir / "dataset"),
        "--output-dir", str(out_dir),
        "--experiment-name", sid,
        "--timestamp", "run",
        "--max-num-iterations", str(iters),
        "--vis", "viewer",
        "--viewer.websocket-port", str(port),
        "--viewer.quit-on-train-completion", "True",
        # Normals are needed for Poisson surface reconstruction at export time.
        "--pipeline.model.predict-normals", "True",
        "nerfstudio-data",
        "--downscale-factor", "1",
    ]

    def on_line(line):
        m = _STEP_RE.match(line)
        if m:
            step, pct = int(m.group(1)), float(m.group(2))
            cols = [c for c in re.split(r"\s{2,}", m.group(3).strip()) if c]
            eta = cols[1] if len(cols) > 1 else None
            store.update(sid, progress=pct, eta=eta,
                         message=f"Training step {step:,} / {iters:,}")
            return
        u = _URL_RE.search(line)
        if u and not store.get(sid).get("viewer_url"):
            store.update(sid, viewer_url=u.group(0).replace("0.0.0.0", "localhost"))
        low = line.lower()
        for key, msg in (("loading data", "Loading images…"), ("caching", "Caching images on the GPU…"),
                         ("setting up", "Setting up the model…"), ("saving checkpoints", "Saving checkpoint…")):
            if key in low:
                store.update(sid, message=msg)

    code = _run_process(sid, args, on_line)
    if _was_cancelled(sid):
        if store.get(sid):
            store.update(sid, status="ready", progress=None, eta=None, viewer_url=None,
                         message="Training stopped — press play to start again.")
        return
    config = find_config(sid)
    ckpts = list(out_dir.glob("**/nerfstudio_models/*.ckpt"))
    if code != 0 or not config or not ckpts:
        tail = _last_error(sid)
        store.update(sid, status="failed", progress=None, eta=None, viewer_url=None,
                     message=f"Training failed (exit {code}). {tail}".strip())
        return
    store.update(sid, status="complete", progress=None, eta=None, viewer_url=None, config=str(config),
                 message="Scan complete — press download to export a mesh.")
    store.append_log(sid, f"Training finished. Config: {config}")


def _last_error(sid: str) -> str:
    for line in reversed(store.read_log(sid, 80).splitlines()):
        if any(k in line for k in ("Error", "error:", "Exception", "CUDA out of memory")):
            return line[:200]
    return "See log for details."


# ---------------------------------------------------------------- mesh export

def _run_export(sid: str):
    s = store.get(sid)
    config = Path(s["config"]) if s.get("config") else find_config(sid)
    if not config or not config.exists():
        store.update(sid, status="complete", message="No trained model found — re-train this session.")
        return
    cfg = settings_mod.load()
    first = cfg.get("export_method", "poisson")
    methods = [first] + [m for m in ("poisson", "tsdf") if m != first]
    export_root = store.session_dir(sid) / "exports"

    for method in methods:
        out = export_root / method
        shutil.rmtree(out, ignore_errors=True)
        store.update(sid, status="exporting", progress=None,
                     message=f"Exporting {method} mesh — starting…")
        store.append_log(sid, f"=== Exporting {method} mesh ===")
        args = [nsenv.tool("ns-export"), method, "--load-config", str(config), "--output-dir", str(out)]
        if method == "poisson":
            args += ["--normal-method", "model_output"]

        def on_line(line, method=method):
            pct = _PCT_RE.search(line)
            text = re.sub(r"\s+", " ", line)[:90]
            store.update(sid, progress=float(pct.group(1)) if pct else None,
                         message=f"Exporting {method} mesh — {text}")

        code = _run_process(sid, args, on_line)
        if _was_cancelled(sid):
            if store.get(sid):
                store.update(sid, status="complete", progress=None, message="Export cancelled.")
            return
        meshes = [p for p in out.glob("*") if p.suffix.lower() in (".ply", ".obj")] if out.exists() else []
        if code == 0 and meshes:
            zip_path = export_root / f"{sid}_{method}_mesh.zip"
            with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
                for f in out.rglob("*"):
                    if f.is_file():
                        z.write(f, f.relative_to(out))
            store.update(sid, status="complete", progress=None, mesh_zip=str(zip_path), mesh_dir=str(out),
                         message=f"Mesh exported ({method}) — click download to save it.")
            store.append_log(sid, f"Mesh written to {out}")
            return
        store.append_log(sid, f"{method} export failed (exit {code}).")

    store.update(sid, status="complete", progress=None,
                 message=f"Mesh export failed. {_last_error(sid)}")
