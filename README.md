# CS302.3-MVP-Development
# Phone App and Viewer app instructions are in PDF's in repository 

# Capture to NeRF (pc processing app)

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
   Finally the phone poses **and lens** are bundle-adjusted with COLMAP (pycolmap, in the NerfStudio
   env): frames are matched with their time neighbours and with frames from other passes that look
   at the same place, and everything is aligned back onto the phone's metric, gravity-up frame.
   ARCore poses drift between passes and the reported lens can be well off for the video, which
   splats show as doubled geometry; on one capture this cut the cross-pass error from ~14 px to
   ~2 px. Adds several minutes; `colmap_refine: false` skips it. The phone poses are kept in
   `transforms_phone.json`, and `colmap_log.txt` has COLMAP's output.
2. **▶ Train** – runs `ns-train splatfacto` (one job at a time on the GPU). Progress, ETA and a
   link to NerfStudio's live viewer appear under the row; click the session ID for the full log.
3. **⤓ Export** – in the Finished table, runs `ns-export gaussian-splat`, then converts the
   splats to real-world metres in Unity's left-handed, Y-up axes (Z mirrored, rotations and
   spherical harmonics adjusted to match). Click again to save `<session>_unity.ply`, then in Unity
   use *Tools → Gaussian Splats → Create GaussianSplatAsset* on it. (nerfacto sessions export a
   Poisson mesh instead, falling back to `tsdf`.) The export is cropped to a
   room-sized box (`room_size_m`, default 10 × 7 × 3 m) placed around the camera path, with the
   floor `camera_height_m` below the phone; set `room_size_m` to `null` to export uncropped.

## Audit log

Interactions and changes are appended to a SQLite database, `%LOCALAPPDATA%\CaptureToNeRF\audit.db`
(it follows `CAPTURE_NERF_DATA`, and outlives deleted sessions). Recorded: app open/close, uploads, scan
starts, stops, exports, downloads and saves, opening a log or the export folder, deleting a session and
rechecking NerfStudio (including rejected attempts), every job status change, SHA-256s of uploaded
capture files and exports, `settings.json` edits, and changes to the app's own code files. The
once-a-second status poll is not recorded.

Each entry's SHA-256 covers its fields and the previous entry's hash, so editing or removing an entry
is detectable. Check the chain or read recent entries with:
```
python -m splatapp.audit verify
python -m splatapp.audit list 50
```
The chain is also checked at every startup. It detects tampering but can't prevent it: someone with
write access could remove the newest entries, or rewrite the whole chain from the point they changed.
Keep a copy of the latest hash somewhere else if you need to rule that out.

## Data

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

