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
    # Crop the exported mesh to a room of this size in metres: [length, width, height].
    # Length follows the longest direction of the camera path. Set to null to export uncropped.
    "room_size_m": [10, 7, 3],
    "room_box_margin_m": 0.25,    # extra space on every side so walls/floor/ceiling aren't clipped
    "camera_height_m": 1.5,       # typical phone height above the floor while capturing
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
