"""Capture checks for prepare: tracking-jump repair, sharp-frame selection, quality report."""
import numpy as np


def pose_times(poses) -> np.ndarray:
    """Seconds since the first row (the phone app writes 't' in nanoseconds); 30 fps fallback."""
    if all("t" in p for p in poses):
        t = np.array([float(p["t"]) for p in poses])
        return (t - t[0]) / 1e9
    return np.arange(len(poses)) / 30.0


def pose_matrices(poses) -> np.ndarray:
    from .prepare import quat_to_rot  # prepare imports this module
    M = np.tile(np.eye(4), (len(poses), 1, 1))
    for k, p in enumerate(poses):
        M[k, :3, :3] = quat_to_rot(p["quat_xyzw"])
        M[k, :3, 3] = p["pos"]
    return M


def _rot_deg(Ra, Rb) -> float:
    return float(np.degrees(np.arccos(np.clip((np.trace(Ra.T @ Rb) - 1) / 2, -1, 1))))


def fix_tracking_jumps(c2w: np.ndarray, t: np.ndarray, max_speed=2.0, max_turn=300.0):
    """ARCore occasionally snaps its pose estimate (relocalisation), so every pose after the snap is
    offset from the ones before and the scene trains as two misaligned copies. A step faster than a
    hand can move the phone is treated as a snap: everything after it is rigidly moved so the pose
    after the snap continues the motion from before it.

    Returns (fixed c2w, list of (row, cm moved, degrees turned))."""
    c2w = c2w.copy()
    jumps = []
    for i in range(len(c2w) - 1):
        dt = max(t[i + 1] - t[i], 1e-3)
        move = np.linalg.norm(c2w[i + 1, :3, 3] - c2w[i, :3, 3])
        turn = _rot_deg(c2w[i, :3, :3], c2w[i + 1, :3, :3])
        if move / dt <= max_speed and turn / dt <= max_turn:
            continue
        # expected pose: previous velocity carried forward, orientation unchanged over one frame
        lo = max(0, i - 3)
        if i > lo:
            vel = (c2w[i, :3, 3] - c2w[lo, :3, 3]) / max(t[i] - t[lo], 1e-3)
        else:
            vel = np.zeros(3)
        expected = np.eye(4)
        expected[:3, :3] = c2w[i, :3, :3]
        expected[:3, 3] = c2w[i, :3, 3] + vel * dt
        corr = expected @ np.linalg.inv(c2w[i + 1])
        c2w[i + 1:] = corr @ c2w[i + 1:]
        jumps.append((i, move * 100, turn))
    return c2w, jumps


def select_sharpest(candidates, window: int):
    """candidates: list of (frame, pose_row, sharpness) in video order. Keeps the sharpest frame of
    each run of `window` consecutive candidates, so blurry frames from fast turns are skipped while
    the spacing along the path stays even."""
    if window <= 1:
        return list(candidates)
    return [max(candidates[k:k + window], key=lambda c: c[2]) for k in range(0, len(candidates), window)]


def quality_report(c2w: np.ndarray, t: np.ndarray, tracked: np.ndarray, jumps, room_size=None):
    """Returns (log lines, warnings for the UI)."""
    P = c2w[tracked, :3, 3]
    tt = t[tracked]
    duration = float(tt[-1] - tt[0]) if len(tt) > 1 else 0.0
    path = float(np.linalg.norm(np.diff(P, axis=0), axis=1).sum()) if len(P) > 1 else 0.0
    R = c2w[tracked, :3, :3]
    dt = np.maximum(np.diff(tt), 1e-3)
    turn = np.array([_rot_deg(R[k], R[k + 1]) for k in range(len(R) - 1)]) / dt if len(R) > 1 else np.zeros(1)
    fwd = -R[:, :, 2]
    pitch = np.degrees(np.arcsin(np.clip(fwd[:, 1], -1, 1)))
    height_span = float(np.ptp(P[:, 1])) if len(P) else 0.0
    turn95 = float(np.percentile(turn, 95))
    up95 = float(np.percentile(pitch, 95))

    lines = [f"Capture: {duration:.0f} s, {path:.1f} m walked, phone height varied {height_span:.2f} m, "
             f"95th-percentile turn speed {turn95:.0f}°/s, steepest upward tilt (95%) {up95:.0f}°."]
    warnings = []
    if duration < 45:
        warnings.append(f"Capture is only {duration:.0f} s — record 1–3 minutes that cover the whole room.")
    if room_size:
        perimeter = 2 * (float(room_size[0]) + float(room_size[1]))
        if path < 0.75 * perimeter:
            warnings.append(f"Only {path:.0f} m walked — walk the whole room (about {perimeter:.0f} m around it).")
    if turn95 > 60:
        warnings.append(f"Phone turned fast ({turn95:.0f}°/s) — turn slower to avoid motion blur.")
    if height_span < 0.5:
        warnings.append("Filmed at one height — add passes higher and lower.")
    if up95 < 20:
        warnings.append("Ceiling barely filmed — tilt the phone up on one pass.")
    if jumps:
        warnings.append(f"{len(jumps)} tracking jump(s) corrected — move steadily and keep the camera on textured areas.")
    for row, cm, deg in jumps:
        lines.append(f"Tracking jump at pose row {row}: {cm:.0f} cm / {deg:.0f}° in one frame — later poses re-joined.")
    return lines, warnings
