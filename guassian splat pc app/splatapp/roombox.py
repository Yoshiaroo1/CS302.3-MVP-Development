"""Crop box for mesh export, sized for a room in real metres.

The phone poses are metric and gravity-aligned (ARCore world: +Y up), but NerfStudio recentres,
reorients and rescales them during training and stores that mapping in
dataparser_transforms.json:  p_ns = scale * (R @ p_world + t).
So the room box is placed in ARCore metres around the capture path, then mapped into the
trained model's frame for ns-export.

Placement:
  * long side (length) follows the long side of the smallest rectangle around the camera path,
  * horizontally centred on the middle of the camera path,
  * the floor sits camera_height_m below the median camera height.
"""
import json
from pathlib import Path

import cv2
import numpy as np


def _camera_positions(dataset_dir: Path) -> np.ndarray:
    data = json.loads((dataset_dir / "transforms.json").read_text(encoding="utf-8"))
    return np.array([np.array(f["transform_matrix"])[:3, 3] for f in data["frames"]], dtype=float)


def _world_box(positions: np.ndarray, size, margin: float, camera_height: float):
    """Returns (center, R, dims) in ARCore world metres. R's columns are the box axes."""
    up = np.array([0.0, 1.0, 0.0])
    xz = positions[:, [0, 2]].astype(np.float32)
    d = np.array([1.0, 0.0])
    if len(xz) >= 3 and np.ptp(xz, axis=0).max() > 1e-3:
        # Smallest rectangle around the camera path; in a room its sides tend to follow the walls.
        (_, _), (w, h), angle = cv2.minAreaRect(xz)
        a = np.radians(angle if w >= h else angle + 90)
        d = np.array([np.cos(a), np.sin(a)])  # along the rectangle's long side
    u_len = np.array([d[0], 0.0, d[1]])
    u_wid = np.cross(up, u_len)          # right-handed: u_len x u_wid = up
    R = np.stack([u_len, u_wid, up], axis=1)

    length, width, height = (float(v) for v in size)
    a, b = positions @ u_len, positions @ u_wid
    floor = float(np.median(positions[:, 1])) - camera_height
    center = (u_len * (a.min() + a.max()) / 2 + u_wid * (b.min() + b.max()) / 2
              + up * (floor + height / 2))
    dims = np.array([length, width, height]) + 2 * margin
    return center, R, dims


def _rpy(R: np.ndarray):
    """Inverse of R = Rz(yaw) @ Ry(pitch) @ Rx(roll), the convention of viser's SO3.from_rpy_radians."""
    pitch = float(np.arcsin(np.clip(-R[2, 0], -1.0, 1.0)))
    roll = float(np.arctan2(R[2, 1], R[2, 2]))
    yaw = float(np.arctan2(R[1, 0], R[0, 0]))
    return roll, pitch, yaw


def export_box(dataset_dir: Path, config: Path, cfg: dict):
    """Returns dict(obb_center, obb_rotation, obb_scale, aabb_min, aabb_max, summary) in the
    trained model's coordinates, or None if room_size_m is not set."""
    size = cfg.get("room_size_m")
    if not size:
        return None
    margin = float(cfg.get("room_box_margin_m", 0.25) or 0)
    camera_height = float(cfg.get("camera_height_m", 1.5))

    positions = _camera_positions(dataset_dir)
    center, R_box, dims = _world_box(positions, size, margin, camera_height)

    dp = json.loads((config.parent / "dataparser_transforms.json").read_text(encoding="utf-8"))
    T, s = np.array(dp["transform"], dtype=float), float(dp["scale"])
    R_dp, t_dp = T[:, :3], T[:, 3]

    c_ns = s * (R_dp @ center + t_dp)
    R_ns = R_dp @ R_box
    dims_ns = s * dims

    # Axis-aligned box enclosing the oriented one, for exporters without OBB support (tsdf).
    corners = np.array([[x, y, z] for x in (-.5, .5) for y in (-.5, .5) for z in (-.5, .5)]) * dims_ns
    corners = corners @ R_ns.T + c_ns

    return {
        "obb_center": tuple(float(v) for v in c_ns),
        "obb_rotation": _rpy(R_ns),
        "obb_scale": tuple(float(v) for v in dims_ns),
        "aabb_min": tuple(float(v) for v in corners.min(axis=0)),
        "aabb_max": tuple(float(v) for v in corners.max(axis=0)),
        "summary": (f"Room box {dims[0]:.2f} x {dims[1]:.2f} x {dims[2]:.2f} m "
                    f"(incl. {margin:.2f} m margin), centre {np.round(center, 2).tolist()} m in capture "
                    f"coordinates, scene scale {s:.4f}."),
    }
