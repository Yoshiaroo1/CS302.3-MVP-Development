"""Remove splat fragments left along the camera path.

Splatfacto sometimes explains a view with small splats floating just in front of the lens, so the
space the phone moved through fills with fragments. Nothing real can be where the phone was, so:

  * splats within clear_m of the path are always removed,
  * splats within suspect_m of the path are removed unless a seed point (a triangulated, real
    surface feature) lies within protect_m - this keeps desks/chairs filmed up close.

Everything is in ARCore world metres. Neighbour searches use a voxel hash (no scipy needed).
"""
from pathlib import Path

import numpy as np

from . import prepare


def load_path(session_dir: Path, spacing: float = 0.02) -> np.ndarray:
    """All tracked phone positions (not just training frames), thinned to ~spacing apart.
    Uses the tracking-jump-corrected path saved by prepare when available."""
    saved = Path(session_dir) / "dataset" / "camera_path.npy"
    if saved.exists():
        P = np.load(saved)
    else:
        _, poses = prepare.parse_poses(Path(session_dir) / "raw" / "poses.jsonl")
        P = np.array([p["pos"] for p in poses if p.get("tracking", "TRACKING") == "TRACKING"], float)
    keep, last = [0], P[0]
    for i in range(1, len(P)):
        if np.linalg.norm(P[i] - last) >= spacing:
            keep.append(i)
            last = P[i]
    return P[keep]


def load_seeds(dataset_dir: Path) -> np.ndarray:
    path = Path(dataset_dir) / "sparse_pc.ply"
    if not path.exists():
        return np.zeros((0, 3))
    with open(path, encoding="ascii") as f:
        for line in f:
            if line.strip() == "end_header":
                break
        return np.loadtxt(f, usecols=(0, 1, 2), ndmin=2)


_OFFSETS = np.array([[i, j, k] for i in (-1, 0, 1) for j in (-1, 0, 1) for k in (-1, 0, 1)])


def _keys(cells: np.ndarray) -> np.ndarray:
    c = cells.astype(np.int64) + (1 << 20)          # shift to non-negative (±1000 km at 1 mm)
    return (c[..., 0] << 42) | (c[..., 1] << 21) | c[..., 2]


def _near(X: np.ndarray, ref: np.ndarray, radius: float) -> np.ndarray:
    """Boolean mask: X within ~radius of any ref point (voxel size = radius, 27 neighbours, so the
    true reach is radius..~2*radius; callers that need an exact distance refine afterwards)."""
    if len(ref) == 0 or len(X) == 0:
        return np.zeros(len(X), bool)
    ref_keys = np.unique(_keys(np.floor(ref / radius)))
    cells = np.floor(X / radius).astype(np.int64)
    hit = np.zeros(len(X), bool)
    for off in _OFFSETS:
        hit |= np.isin(_keys(cells + off), ref_keys, assume_unique=False)
    return hit


def _min_dist(X: np.ndarray, ref: np.ndarray, chunk: int = 4096) -> np.ndarray:
    out = np.empty(len(X))
    for i in range(0, len(X), chunk):
        d = ((X[i:i + chunk, None, :] - ref[None]) ** 2).sum(-1)
        out[i:i + chunk] = np.sqrt(d.min(1))
    return out


def fragment_mask(X: np.ndarray, path: np.ndarray, seeds: np.ndarray,
                  clear_m: float, suspect_m: float, protect_m: float) -> np.ndarray:
    """True for splats (world positions X) that are camera-path fragments."""
    reach = max(clear_m, suspect_m)
    if reach <= 0 or len(path) == 0:
        return np.zeros(len(X), bool)
    cand = np.flatnonzero(_near(X, path, reach))   # coarse voxel pass, then exact distances
    d = np.full(len(X), np.inf)
    if len(cand):
        d[cand] = _min_dist(X[cand], path)
    frag = d < clear_m
    if suspect_m > clear_m:
        suspect = np.flatnonzero((d >= clear_m) & (d < suspect_m))
        if len(suspect):
            protected = _near(X[suspect], seeds, protect_m) if protect_m > 0 else np.zeros(len(suspect), bool)
            frag[suspect[~protected]] = True
    return frag


def mask_for_session(session_dir: Path, cfg: dict):
    """Returns a function world_xyz -> fragment mask, or None if disabled."""
    clear_m = float(cfg.get("splat_path_clear_m", 0) or 0)
    suspect_m = float(cfg.get("splat_path_suspect_m", 0) or 0)
    if clear_m <= 0 and suspect_m <= 0:
        return None
    path = load_path(session_dir)
    seeds = load_seeds(Path(session_dir) / "dataset")
    protect_m = float(cfg.get("splat_seed_protect_m", 0.1) or 0)
    return lambda X: fragment_mask(X, path, seeds, clear_m, suspect_m, protect_m)
