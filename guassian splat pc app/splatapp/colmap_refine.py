"""Bundle-adjust the phone poses with COLMAP (pycolmap). Runs in the NerfStudio env:

    python colmap_refine.py <dataset_dir> <work_dir>

ARCore poses drift between passes (a few cm / a degree or two), which splats show as doubled
geometry. Starting from the phone poses:
  1. SIFT features for every training image,
  2. match pairs picked from the phone poses: neighbours in time, plus frames from other passes
     that stand near each other and look the same way,
  3. triangulate with the poses fixed, then bundle-adjust poses, points and the lens (focal length,
     principal point, OPENCV distortion), dropping bad observations between rounds. The intrinsics
     the phone reports can be well off for the video frames (45 px principal point error on one
     capture); fixing them cut the cross-pass epipolar error from ~14 px to ~2 px,
  4. align the result back onto the phone poses with a similarity transform, so the metric scale
     and gravity-up axes that the rest of the app relies on are kept.

Frames whose correction jumps far from their neighbours' (a bad registration) are left out.

Writes <work_dir>/refined.json: per-image camera-to-world (OpenGL, ARCore world), refined
intrinsics (fl_x, fl_y, cx, cy, k1, k2, p1, p2), stats, and
<work_dir>/points.npy (N x 6: xyz in ARCore world, rgb 0-255) for splatfacto initialisation.
Prints "PROGRESS <pct> <message>" lines for the app.
"""
import json
import sys
import time
from pathlib import Path

import numpy as np
import pycolmap

GL_TO_CV = np.diag([1.0, -1.0, -1.0])


def progress(pct, msg):
    print(f"PROGRESS {pct:.0f} {msg}", flush=True)


def c2w_gl_to_cam_from_world(c2w):
    R = c2w[:3, :3] @ GL_TO_CV
    return pycolmap.Rigid3d(pycolmap.Rotation3d(R.T), -R.T @ c2w[:3, 3])


def cam_from_world_to_c2w_gl(rigid):
    M = rigid.matrix()                      # 3x4 world -> camera (OpenCV)
    R, t = M[:, :3], M[:, 3]
    c2w = np.eye(4)
    c2w[:3, :3] = R.T @ GL_TO_CV
    c2w[:3, 3] = -R.T @ t
    return c2w


def make_pairs(names, c2w, seq=6, max_dist=2.0, max_angle=50.0, per_image=12):
    """Time neighbours plus spatial neighbours (likely from other passes)."""
    C = c2w[:, :3, 3]
    fwd = -c2w[:, :3, 2]
    pairs = set()
    n = len(names)
    for i in range(n):
        for j in range(i + 1, min(n, i + 1 + seq)):
            pairs.add((i, j))
        d = np.linalg.norm(C - C[i], axis=1)
        ang = np.degrees(np.arccos(np.clip(fwd @ fwd[i], -1, 1)))
        ok = np.flatnonzero((d < max_dist) & (ang < max_angle) & (np.abs(np.arange(n) - i) > seq))
        for j in ok[np.argsort(d[ok])][:per_image]:
            pairs.add((min(i, j), max(i, j)))
    return sorted(pairs)


def umeyama(src, dst):
    """Similarity (s, R, t) minimising |s R src + t - dst|."""
    mu_s, mu_d = src.mean(0), dst.mean(0)
    xs, xd = src - mu_s, dst - mu_d
    U, S, Vt = np.linalg.svd(xd.T @ xs / len(src))
    D = np.eye(3)
    if np.linalg.det(U) * np.linalg.det(Vt) < 0:
        D[2, 2] = -1
    R = U @ D @ Vt
    s = np.trace(np.diag(S) @ D) / xs.var(0).sum()
    return s, R, mu_d - s * R @ mu_s


def build_reconstruction(db_path, poses_by_name):
    """Reconstruction holding the database's cameras/images with the given (fixed) poses."""
    rec = pycolmap.Reconstruction()
    with pycolmap.Database.open(str(db_path)) as db:
        for cam in db.read_all_cameras():
            rec.add_camera(cam)
        for rig in db.read_all_rigs():
            rec.add_rig(rig)
        images = {im.image_id: im for im in db.read_all_images()}
        frames = db.read_all_frames()
    for frame in frames:
        # one camera per frame (trivial rig): rig_from_world == cam_from_world
        image_ids = [d.id for d in frame.data_ids]
        frame.rig_from_world = c2w_gl_to_cam_from_world(poses_by_name[images[image_ids[0]].name])
        rec.add_frame(frame)
    for im in images.values():
        rec.add_image(im)
    for frame in frames:
        rec.register_frame(frame.frame_id)
    return rec


def reprojection_filter(rec, max_px):
    """Remove observations with reprojection error above max_px; returns how many."""
    removed = 0
    for pid in list(rec.point3D_ids()):
        p = rec.point3D(pid)
        bad = []
        for el in p.track.elements:
            im = rec.image(el.image_id)
            proj = im.project_point(p.xyz)
            if proj is None or np.linalg.norm(proj - im.points2D[el.point2D_idx].xy) > max_px:
                bad.append((el.image_id, el.point2D_idx))
        if len(p.track.elements) - len(bad) < 2:
            rec.delete_point3D(pid)
            removed += len(p.track.elements)
            continue
        for image_id, idx in bad:
            rec.delete_observation(image_id, idx)
            removed += 1
    return removed


def main(dataset_dir: Path, work: Path, refine_intrinsics: bool = True):
    t0 = time.time()
    work.mkdir(parents=True, exist_ok=True)
    T = json.loads((dataset_dir / "transforms.json").read_text(encoding="utf-8"))
    frames = T["frames"]
    names = [Path(f["file_path"]).name for f in frames]
    c2w = np.array([f["transform_matrix"] for f in frames], dtype=float)
    poses_by_name = dict(zip(names, c2w))
    image_dir = dataset_dir / "images"
    db_path = work / "database.db"
    if db_path.exists():
        db_path.unlink()

    progress(0, f"Extracting features from {len(names)} images…")
    reader = pycolmap.ImageReaderOptions()
    reader.camera_model = "PINHOLE"
    reader.camera_params = ",".join(str(T[k]) for k in ("fl_x", "fl_y", "cx", "cy"))
    ext = pycolmap.FeatureExtractionOptions()
    ext.max_image_size = 1600
    ext.sift.max_num_features = 4096
    pycolmap.extract_features(db_path, image_dir, image_names=names, camera_mode=pycolmap.CameraMode.SINGLE,
                              reader_options=reader, extraction_options=ext)

    pairs = make_pairs(names, c2w)
    (work / "pairs.txt").write_text("\n".join(f"{names[i]} {names[j]}" for i, j in pairs), encoding="utf-8")
    progress(40, f"Matching {len(pairs):,} image pairs…")
    pairing = pycolmap.ImportedPairingOptions()
    pairing.match_list_path = str(work / "pairs.txt")
    pycolmap.match_image_pairs(db_path, pairing_options=pairing)

    progress(75, "Triangulating with the phone poses…")
    rec = build_reconstruction(db_path, poses_by_name)
    tri_out = work / "triangulated"
    tri_out.mkdir(exist_ok=True)
    rec = pycolmap.triangulate_points(rec, db_path, image_dir, tri_out)
    err_before = rec.compute_mean_reprojection_error()
    n_before = rec.num_points3D()

    progress(85, "Bundle adjustment…")
    for cid, cam in rec.cameras.items():           # PINHOLE -> OPENCV so distortion can be refined
        fx, fy, cx, cy = cam.params
        cam.model = pycolmap.CameraModelId.OPENCV
        cam.params = [fx, fy, cx, cy, 0.0, 0.0, 0.0, 0.0]
        rec.cameras[cid] = cam
    ba = pycolmap.BundleAdjustmentOptions()
    ba.refine_focal_length = refine_intrinsics
    ba.refine_principal_point = refine_intrinsics
    ba.refine_extra_params = refine_intrinsics
    ba.ceres.solver_options.max_num_iterations = 400
    ba.print_summary = False
    pycolmap.bundle_adjustment(rec, ba)
    removed = reprojection_filter(rec, 4.0)
    pycolmap.bundle_adjustment(rec, ba)
    removed += reprojection_filter(rec, 2.0)
    pycolmap.bundle_adjustment(rec, ba)
    err_after = rec.compute_mean_reprojection_error()

    # align back onto the phone poses (metric scale, ARCore axes)
    progress(95, "Aligning to the phone poses…")
    refined_c2w = {}
    for im in rec.images.values():
        if im.has_pose:
            refined_c2w[im.name] = cam_from_world_to_c2w_gl(im.cam_from_world())
    order = [n for n in names if n in refined_c2w]
    src = np.array([refined_c2w[n][:3, 3] for n in order])
    dst = np.array([poses_by_name[n][:3, 3] for n in order])
    s, R, t = umeyama(src, dst)
    S = np.eye(4)
    S[:3, :3], S[:3, 3] = R, t
    aligned, moved, turned = {}, [], []
    for n in order:
        m = S @ refined_c2w[n]
        m[:3, 3] = s * R @ refined_c2w[n][:3, 3] + t
        aligned[n] = m
        moved.append(np.linalg.norm(m[:3, 3] - poses_by_name[n][:3, 3]))
        dR = m[:3, :3].T @ poses_by_name[n][:3, :3]
        turned.append(np.degrees(np.arccos(np.clip((np.trace(dR) - 1) / 2, -1, 1))))
    # drift corrections change smoothly along the walk; a frame whose correction differs a lot from
    # its neighbours' was registered wrongly, so leave it out
    shift = np.array([aligned[n][:3, 3] - poses_by_name[n][:3, 3] for n in order])
    outliers = set()
    for k, n in enumerate(order):
        nb = [j for j in range(max(0, k - 4), min(len(order), k + 5)) if j != k]
        if nb and np.linalg.norm(shift[k] - np.median(shift[nb], axis=0)) > 0.25:
            outliers.add(n)
    out = {n: aligned[n].tolist() for n in order if n not in outliers}
    cam = next(iter(rec.cameras.values()))
    intrinsics = dict(zip(("fl_x", "fl_y", "cx", "cy", "k1", "k2", "p1", "p2"), map(float, cam.params)))

    pts = [(s * R @ p.xyz + t, p.color) for p in rec.points3D.values() if p.track.length() >= 3]
    if pts:
        np.save(work / "points.npy", np.array([np.r_[x, c] for x, c in pts], dtype=np.float32))

    stats = {
        "images": len(names), "refined": len(out), "outliers": len(outliers), "pairs": len(pairs),
        "points": len(pts), "reproj_px_before": float(err_before), "reproj_px_after": float(err_after),
        "points_before": int(n_before), "observations_removed": int(removed),
        "pose_change_cm_median": float(np.median(moved) * 100), "pose_change_cm_max": float(np.max(moved) * 100),
        "pose_change_deg_median": float(np.median(turned)), "pose_change_deg_max": float(np.max(turned)),
        "scale_to_phone": float(s), "seconds": round(time.time() - t0),
        "phone_intrinsics": [T["fl_x"], T["fl_y"], T["cx"], T["cy"]],
    }
    (work / "refined.json").write_text(json.dumps({"frames": out, "intrinsics": intrinsics, "stats": stats},
                                                  indent=1), encoding="utf-8")
    progress(100, "Done")
    print("STATS " + json.dumps(stats), flush=True)


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
