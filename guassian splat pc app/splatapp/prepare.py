"""Turn a phone capture (video.mp4 + poses.jsonl [+ intrinsics.json]) into a NerfStudio dataset.

The phone app writes ARCore camera poses (Camera.getPose()): camera-to-world, +X right,
+Y up, camera looking down -Z. That is exactly NerfStudio's OpenGL convention, so poses go
into transforms.json unchanged. Each pose row corresponds to one encoded video frame, but the
two streams can be shifted by a row or two, so the offset is measured visually: the relative
rotation between frame pairs (from feature matching) is compared with the relative rotation
from the poses, and the best-agreeing offset wins.
"""
import json
from pathlib import Path

import cv2
import numpy as np

from . import capture
from . import refine
from .seedpoints import SeedPoints, write_points_ply

# OpenGL camera (NerfStudio/ARCore) <-> OpenCV camera (x right, y down, z forward)
_GL_TO_CV = np.diag([1.0, -1.0, -1.0])


class PrepareError(Exception):
    pass


def quat_to_rot(q_xyzw):
    x, y, z, w = q_xyzw
    n = np.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def parse_poses(path: Path):
    """Returns (intrinsics or None, list of pose dicts in file order)."""
    intrinsics, poses = None, []
    with open(path, encoding="utf-8") as f:
        for n, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except ValueError as e:
                raise PrepareError(f"poses.jsonl line {n} is not valid JSON: {e}")
            if "intrinsics" in rec:
                intrinsics = rec["intrinsics"]
            elif "quat_xyzw" in rec and "pos" in rec:
                poses.append(rec)
    if not poses:
        raise PrepareError("poses.jsonl contains no pose rows (expected 'pos' and 'quat_xyzw').")
    return intrinsics, poses


def load_intrinsics(intrinsics_file, embedded):
    if intrinsics_file and Path(intrinsics_file).exists():
        data = json.loads(Path(intrinsics_file).read_text(encoding="utf-8"))
        data = data.get("intrinsics", data)
    else:
        data = embedded
    if not data:
        raise PrepareError("No camera intrinsics: upload intrinsics.json or include them in poses.jsonl.")
    try:
        return {k: float(data[k]) for k in ("fx", "fy", "cx", "cy", "width", "height")}
    except KeyError as e:
        raise PrepareError(f"Intrinsics missing field {e}.")


def fit_intrinsics(intr, vw, vh):
    """Scale intrinsics measured at (width,height) to the video resolution.

    Handles a uniform resize, or a resize plus centre crop when the aspect ratio differs
    (e.g. a 4:3 sensor image recorded as 16:9 video)."""
    iw, ih = intr["width"], intr["height"]
    if (iw > ih) != (vw > vh) and abs(iw - ih) > 1:
        raise PrepareError(
            f"Video is {vw}x{vh} but intrinsics are {int(iw)}x{int(ih)} (rotated). "
            "Record video in the camera sensor's native orientation.")
    s = max(vw / iw, vh / ih)
    return {
        "fl_x": intr["fx"] * s,
        "fl_y": intr["fy"] * s,
        "cx": intr["cx"] * s - (iw * s - vw) / 2,
        "cy": intr["cy"] * s - (ih * s - vh) / 2,
        "w": int(vw),
        "h": int(vh),
    }


ANALYSIS_MAX_DIM = 960


def iter_frames(video: Path):
    cap = cv2.VideoCapture(str(video))
    if not cap.isOpened():
        raise PrepareError("Could not open the video file.")
    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                return
            yield frame
    finally:
        cap.release()


def analyse_frames(video: Path, log, progress):
    """Pass 1: downscaled grayscale frames (for alignment) + sharpness, without keeping full frames."""
    total = int(cv2.VideoCapture(str(video)).get(cv2.CAP_PROP_FRAME_COUNT)) or 0
    gray, sharpness, size, scale = [], [], None, 1.0
    for frame in iter_frames(video):
        if size is None:
            size = (frame.shape[1], frame.shape[0])
            scale = min(1.0, ANALYSIS_MAX_DIM / max(size))
        g = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        if scale < 1.0:
            g = cv2.resize(g, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
        gray.append(g)
        sharpness.append(float(cv2.Laplacian(g, cv2.CV_64F).var()))
        if len(gray) % 50 == 0:
            progress(5 + 20 * len(gray) / max(total, len(gray)), f"Decoding video ({len(gray)} frames)…")
    if not gray:
        raise PrepareError("The video contains no decodable frames.")
    return gray, sharpness, size, scale


def _rot_angle_deg(R):
    return float(np.degrees(np.arccos(np.clip((np.trace(R) - 1) / 2, -1, 1))))


def estimate_offset(gray, poses, K, log, max_offset=3):
    """Best k such that video frame i matches pose row i+k."""
    n = len(gray)
    tracking = [p.get("tracking", "TRACKING") == "TRACKING" for p in poses]
    sift = cv2.SIFT_create(1500)
    matcher = cv2.BFMatcher()
    gap = 8
    starts = [i for i in range(max_offset, n - gap - max_offset, max(1, (n - 2 * max_offset) // 16))]
    measured = []
    feats = {}

    def feat(i):
        if i not in feats:
            feats[i] = sift.detectAndCompute(gray[i], None)
        return feats[i]

    for a in starts:
        b = a + gap
        (k1, d1), (k2, d2) = feat(a), feat(b)
        if d1 is None or d2 is None or len(k1) < 30 or len(k2) < 30:
            continue
        good = [m for m, m2 in (p for p in matcher.knnMatch(d1, d2, k=2) if len(p) == 2)
                if m.distance < 0.7 * m2.distance]
        if len(good) < 40:
            continue
        p1 = np.float32([k1[m.queryIdx].pt for m in good])
        p2 = np.float32([k2[m.trainIdx].pt for m in good])
        E, mask = cv2.findEssentialMat(p1, p2, K, cv2.RANSAC, 0.999, 1.0)
        # recoverPose's own inlier mask rejects most points when the baseline is a few cm,
        # so judge reliability from the RANSAC inliers instead.
        if E is None or mask is None or int(mask.sum()) < 30:
            continue
        _, R, _, _ = cv2.recoverPose(np.ascontiguousarray(E[:3]), p1, p2, K)
        if _rot_angle_deg(R) < 1.0:
            continue  # too little rotation to tell offsets apart
        measured.append((a, b, R))

    if len(measured) < 3:
        log("Not enough texture/motion to verify frame alignment; assuming offset 0.")
        return 0, None

    results = {}
    for off in range(-max_offset, max_offset + 1):
        errs = []
        for a, b, R in measured:
            ia, ib = a + off, b + off
            if not (0 <= ia < len(poses) and 0 <= ib < len(poses)) or not (tracking[ia] and tracking[ib]):
                continue
            Ra, Rb = quat_to_rot(poses[ia]["quat_xyzw"]), quat_to_rot(poses[ib]["quat_xyzw"])
            R_pose = _GL_TO_CV @ (Rb.T @ Ra) @ _GL_TO_CV
            errs.append(_rot_angle_deg(R.T @ R_pose))
        if len(errs) >= 3:
            results[off] = float(np.median(errs))
    if not results:
        return 0, None
    best = min(results, key=results.get)
    log("Frame/pose alignment error by offset: " +
        ", ".join(f"{k:+d}: {v:.2f}°" for k, v in sorted(results.items())))
    if results[best] > 5:
        log(f"WARNING: poses agree poorly with the video ({results[best]:.1f}° median error). "
            "Results may be blurry.")
    return best, results[best]


def prepare(session_dir: Path, cfg: dict, log, progress):
    raw = session_dir / "raw"
    video, poses_file = raw / "video.mp4", raw / "poses.jsonl"
    out = session_dir / "dataset"
    img_dir = out / "images"

    progress(2, "Reading poses…")
    embedded, poses = parse_poses(poses_file)
    intr = load_intrinsics(raw / "intrinsics.json", embedded)
    n_track = sum(p.get("tracking", "TRACKING") == "TRACKING" for p in poses)
    log(f"{len(poses)} pose rows, {n_track} with TRACKING state.")
    if n_track < 10:
        raise PrepareError(f"Only {n_track} poses have tracking — move the phone more slowly and re-capture.")

    progress(5, "Decoding video…")
    gray, sharpness, (vw, vh), scale = analyse_frames(video, log, progress)
    n_frames = len(gray)
    log(f"Video: {n_frames} frames at {vw}x{vh}.")
    if abs(n_frames - len(poses)) > max(5, 0.05 * n_frames):
        log(f"WARNING: {n_frames} video frames but {len(poses)} poses — expected roughly one pose per frame.")
    cam = fit_intrinsics(intr, vw, vh)
    K = np.array([[cam["fl_x"] * scale, 0, cam["cx"] * scale],
                  [0, cam["fl_y"] * scale, cam["cy"] * scale], [0, 0, 1]])

    progress(25, "Aligning poses to frames…")
    offset, err = estimate_offset(gray, poses, K, log)
    del gray
    log(f"Using frame i ↔ pose row i{offset:+d}.")

    progress(50, "Checking tracking…")
    times = capture.pose_times(poses)
    tracked = np.array([p.get("tracking", "TRACKING") == "TRACKING" for p in poses])
    c2ws, jumps = capture.fix_tracking_jumps(capture.pose_matrices(poses), times)
    lines, warnings = capture.quality_report(c2ws, times, tracked, jumps, cfg.get("room_size_m"))
    for line in lines + [f"WARNING: {w}" for w in warnings]:
        log(line)
    # full (jump-corrected) phone path, used to remove camera-path fragments at export
    out.mkdir(parents=True, exist_ok=True)
    np.save(out / "camera_path.npy", c2ws[tracked, :3, 3])

    progress(55, "Selecting sharp, tracked frames…")
    candidates = []
    for i in range(n_frames):
        j = i + offset
        if 0 <= j < len(poses) and poses[j].get("tracking", "TRACKING") == "TRACKING":
            candidates.append((i, j, sharpness[i]))
    if len(candidates) < 10:
        raise PrepareError("Fewer than 10 video frames have a tracked pose.")

    ratio = float(cfg.get("blur_drop_ratio", 0) or 0)
    if ratio > 0:
        med = float(np.median([c[2] for c in candidates]))
        sharp_enough = [c for c in candidates if c[2] >= ratio * med]
        if len(sharp_enough) >= 0.6 * len(candidates):
            log(f"Dropped {len(candidates) - len(sharp_enough)} blurry frames.")
            candidates = sharp_enough
    max_frames = int(cfg.get("max_frames", 0) or 0)
    window = max(int(cfg.get("sharp_window", 1) or 1),
                 -(-len(candidates) // max_frames) if max_frames else 1)
    if window > 1:
        candidates = capture.select_sharpest(candidates, window)
        log(f"Kept the sharpest of every {window} frames: {len(candidates)} frames.")

    progress(65, "Writing images…")
    img_dir.mkdir(parents=True, exist_ok=True)
    quality = int(cfg.get("jpeg_quality", 95))
    wanted = {i: j for i, j, _ in candidates}
    frame_entries = []
    seeds = SeedPoints(K, scale) if cfg.get("seed_points", True) else None
    for i, frame in enumerate(iter_frames(video)):
        if i not in wanted:
            continue
        name = f"frame_{i:05d}.jpg"
        if not cv2.imwrite(str(img_dir / name), frame, [cv2.IMWRITE_JPEG_QUALITY, quality]):
            raise PrepareError(f"Could not write {img_dir / name}.")
        c2w = c2ws[wanted[i]]
        frame_entries.append({"file_path": f"images/{name}", "transform_matrix": c2w.tolist()})
        if seeds:
            seeds.add(frame, c2w)
        if len(frame_entries) % 25 == 0:
            progress(65 + 30 * len(frame_entries) / len(wanted),
                     f"Writing images{' + seed points' if seeds else ''} ({len(frame_entries)}/{len(wanted)})…")

    transforms = {
        "camera_model": "OPENCV",
        **cam,
        "k1": 0.0, "k2": 0.0, "p1": 0.0, "p2": 0.0,
        "frames": frame_entries,
    }
    if seeds:
        n_pts = seeds.write_ply(out / "sparse_pc.ply")
        if n_pts >= 1000:
            transforms["ply_file_path"] = "sparse_pc.ply"
            log(f"Seed point cloud: {n_pts:,} triangulated points.")
        else:
            log(f"Only {n_pts} seed points triangulated; splatfacto will start from random points.")
    (out / "transforms.json").write_text(json.dumps(transforms, indent=2), encoding="utf-8")

    if cfg.get("colmap_refine", True):
        result = refine.run(out, log, progress)
        if result:
            refined, points, intrinsics, _ = result
            (out / "transforms_phone.json").write_text(json.dumps(transforms, indent=2), encoding="utf-8")
            transforms.update(intrinsics)        # lens refined by bundle adjustment (OPENCV model)
            row_of = {f"frame_{i:05d}.jpg": j for i, j in wanted.items()}
            kept = [e for e in frame_entries if Path(e["file_path"]).name in refined]
            for e in kept:
                e["transform_matrix"] = refined[Path(e["file_path"]).name].tolist()
            if len(kept) < len(frame_entries):
                log(f"Dropped {len(frame_entries) - len(kept)} frames COLMAP couldn't place.")
            transforms["frames"] = frame_entries = kept
            if points is not None and len(points) >= 1000:
                n_pts = write_points_ply(out / "sparse_pc.ply", points[:, :3], points[:, 3:])
                transforms["ply_file_path"] = "sparse_pc.ply"
                log(f"Seed point cloud replaced by {n_pts:,} bundle-adjusted COLMAP points.")
            # carry each training frame's correction to the nearby rows of the full phone path
            rows = np.array(sorted(row_of[n] for n in refined if n in row_of))
            delta = {row_of[n]: refined[n] @ np.linalg.inv(c2ws[row_of[n]]) for n in refined if n in row_of}
            nearest = rows[np.clip(np.searchsorted(rows, np.arange(len(c2ws))), 0, len(rows) - 1)]
            corrected = np.array([delta[r] @ m for r, m in zip(nearest, c2ws)])
            np.save(out / "camera_path.npy", corrected[tracked, :3, 3])
            (out / "transforms.json").write_text(json.dumps(transforms, indent=2), encoding="utf-8")
    log(f"Dataset ready: {len(frame_entries)} frames → {out}")
    return {"frames": len(frame_entries), "frames_total": n_frames, "pose_offset": offset,
            "pose_error_deg": err, "warnings": warnings}
