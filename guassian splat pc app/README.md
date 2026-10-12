# Capture to NeRF

Desktop app that turns a phone capture (`video.mp4` + `poses.jsonl` [+ `intrinsics.json`, `imu.json`])
into a NerfStudio **nerfacto** scan and exports it as a 3D mesh.

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
2. **▶ Train** – runs `ns-train nerfacto` (one job at a time on the GPU). Progress, ETA and a
   link to NerfStudio's live viewer appear under the row; click the session ID for the full log.
3. **⤓ Export** – in the Finished table, runs `ns-export poisson` (falls back to `tsdf`) and
   zips the textured `.obj` + `.ply`. Click again to save the zip. The mesh is cropped to a
   room-sized box (`room_size_m`, default 10 × 7 × 3 m) placed around the camera path, with the
   floor `camera_height_m` below the phone; set `room_size_m` to `null` to export uncropped.

Session data lives in `%LOCALAPPDATA%\CaptureToNeRF\sessions` (kept out of OneDrive);
override with the `CAPTURE_NERF_DATA` env var. Tunables (iterations, frame cap, blur filter,
export method) are in `settings.json`.
