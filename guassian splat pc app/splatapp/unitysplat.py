"""Convert NerfStudio's splat.ply into a PLY ready for aras-p/UnityGaussianSplatting.

ns-export gaussian-splat writes splats in the trained model's frame. Training uses
--orientation-method none, so that frame is the phone's ARCore world (metres, right-handed,
+Y up) recentred and uniformly scaled:  p_model = scale * (p_world + t).  This undoes that,
then mirrors Z to get Unity's left-handed, +Y up axes (the Unity importer copies x/y/z,
rot_0..3 (w,x,y,z) and SH coefficients without converting them).

Mirroring Z (M = diag(1, 1, -1)):
  * position:    z -> -z
  * quaternion:  (w, x, y, z) -> (w, -x, -y, z)     (rotation R -> M R M)
  * SH:          basis functions that are odd in z change sign
"""
import json
from pathlib import Path

import numpy as np

# Within each colour channel's f_rest block (degree 1..3, INRIA/gsplat order), the
# coefficients whose basis is odd in z: yz, xz (l=2) and xyz, z(..), z(xx-yy) (l=3), z (l=1).
_Z_ODD_REST = (1, 4, 6, 9, 11, 13)

_PLY_TYPES = {"float": "<f4", "float32": "<f4", "uchar": "u1", "uint8": "u1", "int": "<i4", "double": "<f8"}


def _read_ply(path: Path):
    with open(path, "rb") as f:
        header = []
        while True:
            line = f.readline()
            if not line:
                raise ValueError(f"{path.name}: truncated PLY header")
            line = line.decode("ascii").strip()
            header.append(line)
            if line == "end_header":
                break
        if "format binary_little_endian 1.0" not in header:
            raise ValueError(f"{path.name}: expected a binary little-endian PLY")
        count, props = 0, []
        for line in header:
            parts = line.split()
            if parts[:2] == ["element", "vertex"]:
                count = int(parts[2])
            elif parts[0] == "property":
                props.append((parts[2], _PLY_TYPES[parts[1]]))
        data = np.fromfile(f, dtype=np.dtype(props), count=count)
    return header, data


def _write_ply(path: Path, header, data):
    with open(path, "wb") as f:
        f.write(("\n".join(header) + "\n").encode("ascii"))
        data.tofile(f)


def _cleanup(v, max_needle_ratio: float, min_opacity: float):
    """Mask of splats to keep. Needles = longest axis >> middle axis (flat discs on surfaces,
    which have one tiny axis, are kept); faint = sigmoid(opacity) below min_opacity."""
    keep = np.ones(len(v), bool)
    if max_needle_ratio:
        s = np.sort(np.stack([v[f"scale_{i}"] for i in range(3)], axis=1), axis=1)  # log scales
        keep &= (s[:, 2] - s[:, 1]) <= np.log(max_needle_ratio)
    if min_opacity:
        keep &= v["opacity"] >= np.log(min_opacity / (1 - min_opacity))  # logit
    return keep


def convert(src: Path, dst: Path, dataparser_transforms: Path,
            max_needle_ratio: float = 0, min_opacity: float = 0, fragment_fn=None) -> dict:
    """Writes the Unity-ready PLY to dst. fragment_fn(world_xyz) -> bool mask of camera-path
    fragments to drop (see pathclean). Returns dict(kept, needles, faint, path_fragments)."""
    dp = json.loads(Path(dataparser_transforms).read_text(encoding="utf-8"))
    T, s = np.array(dp["transform"], dtype=np.float64), float(dp["scale"])
    if not np.allclose(T[:, :3], np.eye(3), atol=1e-4):
        raise ValueError("Model was trained with a rotated frame; retrain with --orientation-method none.")
    t = T[:, 3]

    header, v = _read_ply(src)
    names = v.dtype.names

    needles = len(v) - int(_cleanup(v, max_needle_ratio, 0).sum())
    keep = _cleanup(v, max_needle_ratio, min_opacity)
    faint = len(v) - needles - int(keep.sum())
    v = v[keep]

    # model frame -> ARCore metres
    for i, axis in enumerate("xyz"):
        v[axis] = v[axis] / s - t[i]
    for i in range(3):
        v[f"scale_{i}"] -= np.log(s)  # scales are stored as log

    fragments = 0
    if fragment_fn is not None:
        frag = fragment_fn(np.stack([v["x"], v["y"], v["z"]], axis=1).astype(np.float64))
        fragments = int(frag.sum())
        v = v[~frag]
    header = [f"element vertex {len(v)}" if h.startswith("element vertex") else h for h in header]

    # ARCore (right-handed) -> Unity (left-handed): mirror Z
    v["z"] = -v["z"]
    v["rot_1"] = -v["rot_1"]
    v["rot_2"] = -v["rot_2"]
    rest = sorted((n for n in names if n.startswith("f_rest_")), key=lambda n: int(n[7:]))
    per_channel = len(rest) // 3
    for c in range(3):
        for j in _Z_ODD_REST:
            if j < per_channel:
                name = rest[c * per_channel + j]
                v[name] = -v[name]
    if "nz" in names:
        v["nz"] = -v["nz"]

    _write_ply(dst, header, v)
    return {"kept": len(v), "needles": needles, "faint": faint, "path_fragments": fragments}
