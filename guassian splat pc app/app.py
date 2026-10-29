"""Desktop entry point: starts the local server and opens the app window.

    python app.py            # native window (pywebview), falls back to an Edge app window
    python app.py --browser  # open in the default browser instead
"""
import os
import shutil
import socket
import subprocess
import sys
import threading
import time
import webbrowser
from pathlib import Path

from werkzeug.serving import make_server

from splatapp import APP_DATA, jobs, store
from splatapp.server import app

TITLE = "Capture to NeRF"


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class JsApi:
    """Exposed to the page as window.pywebview.api (native window only)."""

    def __init__(self):
        # Underscore keeps pywebview from exposing (and recursively walking) the native window to JS.
        self._window = None

    def save_mesh(self, sid):
        import webview
        s = store.get(sid)
        if not s or not s.get("mesh_zip") or not Path(s["mesh_zip"]).exists():
            return {"ok": False, "error": "Nothing exported yet."}
        src = Path(s["mesh_zip"])
        dialog = getattr(getattr(webview, "FileDialog", None), "SAVE", None) or webview.SAVE_DIALOG
        kind = "Gaussian splat (*.ply)" if src.suffix.lower() == ".ply" else "Zip archive (*.zip)"
        result = self._window.create_file_dialog(dialog, save_filename=src.name, file_types=(kind,))
        if not result:
            return {"ok": False, "cancelled": True}
        dest = result if isinstance(result, str) else result[0]
        shutil.copyfile(src, dest)
        return {"ok": True, "path": dest}

    def open_folder(self, sid):
        s = store.get(sid)
        folder = s.get("mesh_dir") if s else None
        if folder and Path(folder).exists():
            os.startfile(folder)
            return True
        return False


def find_edge():
    for base in (os.environ.get("PROGRAMFILES(X86)"), os.environ.get("PROGRAMFILES"), os.environ.get("LOCALAPPDATA")):
        if base:
            p = Path(base) / "Microsoft" / "Edge" / "Application" / "msedge.exe"
            if p.exists():
                return str(p)
    return None


def main():
    store.load_all()
    port = free_port()
    server = make_server("127.0.0.1", port, app, threaded=True)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{port}/"
    print(f"{TITLE} running at {url}")

    try:
        if "--browser" in sys.argv:
            raise ImportError
        import webview
        api = JsApi()
        api._window = webview.create_window(TITLE, url, js_api=api, width=1280, height=900, min_size=(760, 600))
        webview.start()
    except ImportError:
        edge = find_edge()
        if edge and "--browser" not in sys.argv:
            profile = APP_DATA / "edge-profile"
            proc = subprocess.Popen([edge, f"--app={url}", f"--user-data-dir={profile}",
                                     "--window-size=1280,900", "--no-first-run"])
            proc.wait()
        else:
            webbrowser.open(url)
            print("Press Ctrl+C to quit.")
            try:
                while True:
                    time.sleep(1)
            except KeyboardInterrupt:
                pass
    finally:
        jobs.shutdown()
        server.shutdown()


if __name__ == "__main__":
    main()
