"""Run colmap_refine.py (bundle adjustment of the phone poses) in the NerfStudio env and apply it."""
import json
import subprocess
from pathlib import Path

import numpy as np

from . import nsenv

SCRIPT = Path(__file__).with_name("colmap_refine.py")
_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def _python():
    env = nsenv.find()
    if not env:
        return None
    for p in (env["env_root"] / "python.exe", env["env_root"] / "bin" / "python", env["bin_dir"] / "python.exe"):
        if p.exists():
            return p
    return None


def available() -> bool:
    py = _python()
    if not py:
        return False
    r = subprocess.run([str(py), "-c", "import pycolmap"], capture_output=True, creationflags=_NO_WINDOW)
    return r.returncode == 0


def run(dataset_dir: Path, log, progress, min_fraction=0.8):
    """Returns (refined c2w by image name, points (N x 6) or None, intrinsics, stats), or None if
    refinement was skipped or failed."""
    py = _python()
    if not py or not available():
        log("Pose refinement skipped: pycolmap isn't installed in the NerfStudio env (see setup_nerfstudio.ps1).")
        return None
    work = dataset_dir / "colmap"
    proc = subprocess.Popen([str(py), "-X", "utf8", str(SCRIPT), str(dataset_dir), str(work)],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                            env=nsenv.subprocess_env(), text=True, encoding="utf-8", errors="replace",
                            creationflags=_NO_WINDOW)
    tail = []
    with open(work.parent / "colmap_log.txt", "w", encoding="utf-8") as raw_log:
        for line in proc.stdout:
            raw_log.write(line)
            line = line.rstrip()
            tail = (tail + [line])[-15:]
            if line.startswith("PROGRESS "):
                _, pct, msg = line.split(" ", 2)
                progress(float(pct), f"Refining poses — {msg}")
    if proc.wait() != 0 or not (work / "refined.json").exists():
        log("WARNING: pose refinement failed; training on the phone poses. Last output:\n  " + "\n  ".join(tail))
        return None
    data = json.loads((work / "refined.json").read_text(encoding="utf-8"))
    st = data["stats"]
    log(f"Pose refinement: {st['refined']}/{st['images']} images, {st['pairs']:,} pairs, {st['points']:,} points; "
        f"reprojection error {st['reproj_px_before']:.2f} → {st['reproj_px_after']:.2f} px; poses moved "
        f"{st['pose_change_cm_median']:.1f} cm / {st['pose_change_deg_median']:.2f}° median "
        f"(max {st['pose_change_cm_max']:.0f} cm / {st['pose_change_deg_max']:.1f}°); "
        f"{st['outliers']} badly placed frames left out; {st['seconds']} s.")
    intr = data["intrinsics"]
    ph = st["phone_intrinsics"]
    log(f"Lens: focal {ph[0]:.0f} → {intr['fl_x']:.0f} px, centre ({ph[2]:.0f}, {ph[3]:.0f}) → "
        f"({intr['cx']:.0f}, {intr['cy']:.0f}), distortion k1 {intr['k1']:+.3f} k2 {intr['k2']:+.3f}.")
    if st["refined"] < min_fraction * st["images"]:
        log(f"WARNING: only {st['refined']}/{st['images']} images refined; keeping the phone poses.")
        return None
    frames = {name: np.array(m) for name, m in data["frames"].items()}
    points = np.load(work / "points.npy") if (work / "points.npy").exists() else None
    return frames, points, data["intrinsics"], st
