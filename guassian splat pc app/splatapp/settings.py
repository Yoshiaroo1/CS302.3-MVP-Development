"""User-editable settings, stored in settings.json next to app.py."""
import json

from . import SETTINGS_FILE

DEFAULTS = {
    # Folder containing ns-train(.exe). Leave empty to auto-detect a conda env / venv.
    "nerfstudio_bin": "",
    # Conda env name searched for during auto-detection.
    "conda_env": "nerfstudio",
    # "splatfacto" (Gaussian splat .ply for UnityGaussianSplatting) or "nerfacto" (mesh export).
    "train_method": "splatfacto",
    # Training length. 30000 is NerfStudio's default; 10000 is a quick preview.
    "max_iterations": 30000,
    # Triangulate a sparse point cloud from the phone poses to initialise splatfacto.
    "seed_points": True,
    # Bundle-adjust the phone poses with COLMAP (pycolmap in the NerfStudio env) before training.
    # Fixes drift between passes that shows up as doubled geometry. Adds several minutes to prepare.
    "colmap_refine": True,
    # splatfacto training: penalise needle-shaped splats, refine phone poses ("off" to disable),
    # and cache images in system RAM ("cpu") or VRAM ("gpu").
    "splat_scale_regularization": True,
    "splat_camera_optimizer": "SO3xR3",
    "splat_cache_images": "cpu",
    # Per-frame exposure/white-balance correction during training. Off by default: the learned
    # per-frame corrections let the base colours drift (a grey cast in Unity, which has no correction).
    "splat_bilateral_grid": False,
    # Export cleanup: drop splats whose longest axis is more than this many times the middle one
    # (needles; flat discs on surfaces are kept), or that are fainter than this opacity. 0 = keep.
    "splat_max_needle_ratio": 15,
    "splat_min_opacity": 0.05,
    # Camera-path fragments (metres): splats closer than clear_m to where the phone moved are always
    # removed; those closer than suspect_m are removed unless a seed point (real surface) is within
    # ~protect_m. 0 disables.
    "splat_path_clear_m": 0.1,
    "splat_path_suspect_m": 0.3,
    "splat_seed_protect_m": 0.1,
    # Port for NerfStudio's live web viewer while training.
    "viewer_port": 7007,
    # Frame selection.
    "max_frames": 600,            # cap on training frames
    "sharp_window": 1,            # keep the sharpest of every N frames; raised automatically when the video
                                  # has more than max_frames, so long videos drop their blurriest frames
    "blur_drop_ratio": 0.35,      # drop frames blurrier than this fraction of the median sharpness (0 = keep all)
    "jpeg_quality": 95,
    # Mesh export (nerfacto only): "poisson" (needs normals, trained with predict-normals) or "tsdf".
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
