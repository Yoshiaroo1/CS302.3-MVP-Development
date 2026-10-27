# Capture to NeRF

Desktop app that turns a phone capture (`video.mp4` + `poses.jsonl` [+ `intrinsics.json`, `imu.json`])
into a NerfStudio **splatfacto** Gaussian splat and exports a `.ply` for
[UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)
(set `train_method` to `nerfacto` in `settings.json` for the older mesh pipeline).

## Setup (once)

1. App dependencies (your normal Python):
   ```
   python -m pip install -r requirements.txt
   ```
2. NerfStudio (its own Python 3.10 conda env, with CUDA PyTorch):
   ```
   powershell -ExecutionPolicy Bypass -File .\setup_nerfstudio.ps1
   ```
   Installs Miniconda via winget if needed. If you already have NerfStudio somewhere else,
   set `nerfstudio_bin` in `settings.json` to the folder containing `ns-train.exe`.

## Run

Double-click `run.bat` (or `python app.py`). `python app.py --browser` opens it in your browser instead.

## Pipeline

1. **Upload & Prepare** – frames are decoded, aligned to pose rows, blurry and untracked frames
   are dropped, and a NerfStudio `transforms.json` is written. The phone's ARCore poses use the
   same OpenGL camera convention as NerfStudio, so no COLMAP step is needed. The frame↔pose
   offset is measured from the video itself (feature-matched rotations vs. pose rotations).
   A sparse seed point cloud (`sparse_pc.ply`) is triangulated from SIFT matches between nearby
   frames using the known poses, so splatfacto doesn't start from random points.
   Tracking jumps (ARCore snapping its pose faster than a hand can move) are re-joined; when the
   video has more than `max_frames` frames, the sharpest of each run is kept instead of evenly spaced
   ones; and a capture report warns when the video is short,
   doesn't walk the room, turns fast, stays at one height or misses the ceiling. The warnings show
   under the session and in its log.
2. **▶ Train** – runs `ns-train splatfacto` (one job at a time on the GPU). Progress, ETA and a
   link to NerfStudio's live viewer appear under the row; click the session ID for the full log.
3. **⤓ Export** – in the Finished table, runs `ns-export gaussian-splat`, then converts the
   splats to real-world metres in Unity's left-handed, Y-up axes (Z mirrored, rotations and
   spherical harmonics adjusted to match). Click again to save `<session>_unity.ply`, then in Unity
   use *Tools → Gaussian Splats → Create GaussianSplatAsset* on it. (nerfacto sessions export a
   Poisson mesh instead, falling back to `tsdf`.) The export is cropped to a
   room-sized box (`room_size_m`, default 10 × 7 × 3 m) placed around the camera path, with the
   floor `camera_height_m` below the phone; set `room_size_m` to `null` to export uncropped.

Session data lives in `%LOCALAPPDATA%\CaptureToNeRF\sessions` (kept out of OneDrive);
override with the `CAPTURE_NERF_DATA` env var. Tunables (iterations, frame cap, blur filter,
export method) are in `settings.json`.

Splat quality settings: splatfacto trains with scale regularisation (stops needle-shaped splats),
pose refinement (`SO3xR3`, corrects small ARCore pose errors) and images cached in system RAM so
600 frames fit on an 8 GB GPU. On export, splats more than `splat_max_needle_ratio` times longer
than they are wide, or fainter than `splat_min_opacity`, are removed (0 disables either).
Fragments along the camera path are removed too: anything within `splat_path_clear_m` (10 cm) of
where the phone moved, and anything within `splat_path_suspect_m` (30 cm) that has no seed point
(a triangulated real surface) within `splat_seed_protect_m`, so objects filmed up close survive.
