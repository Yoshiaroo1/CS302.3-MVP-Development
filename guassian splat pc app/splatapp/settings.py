"""User-editable settings, stored in settings.json next to app.py."""
import json

from . import SETTINGS_FILE

DEFAULTS = {
    # Folder containing ns-train(.exe). Leave empty to auto-detect a conda env / venv.
    "nerfstudio_bin": "",
    # Conda env name searched for during auto-detection.
    "conda_env": "nerfstudio",
    # nerfacto training length. 30000 is NerfStudio's default; 10000 is a quick preview.
    "max_iterations": 30000,
    # Port for NerfStudio's live web viewer while training.
    "viewer_port": 7007,
    # Frame selection.
    "max_frames": 300,            # uniformly subsample above this many frames
    "blur_drop_ratio": 0.35,      # drop frames sharper than this fraction of the median (0 = keep all)
    "jpeg_quality": 95,
    # Mesh export: "poisson" (needs normals, trained with predict-normals) or "tsdf".
    "export_method": "poisson",
}


def load() -> dict:
    settings = dict(DEFAULTS)
    if SETTINGS_FILE.exists():
        try:
            settings.update(json.loads(SETTINGS_FILE.read_text(encoding="utf-8")))
        except (OSError, ValueError):
            pass
    else:
        try:
            SETTINGS_FILE.write_text(json.dumps(DEFAULTS, indent=2), encoding="utf-8")
        except OSError:
            pass
    return settings
