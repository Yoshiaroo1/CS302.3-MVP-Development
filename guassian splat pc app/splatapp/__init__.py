"""Capture-to-NeRF desktop app: phone captures -> NerfStudio splatfacto -> Unity Gaussian splat."""
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
STATIC_DIR = ROOT / "static"
SETTINGS_FILE = ROOT / "settings.json"

# Session data (frames, checkpoints, meshes) is large, so it lives outside the project
# folder — which sits in OneDrive — unless CAPTURE_NERF_DATA points elsewhere.
APP_DATA = Path(os.environ.get("CAPTURE_NERF_DATA")
                or Path(os.environ.get("LOCALAPPDATA", Path.home())) / "CaptureToNeRF")
DATA_DIR = APP_DATA / "sessions"
