"""Sparse point cloud for splatfacto initialisation, triangulated from the known phone poses.

Splatfacto starts from random Gaussians when the dataset has no point cloud, which gives a
room full of floaters. Here each kept frame is matched (SIFT) against a few earlier kept frames
and the matches are triangulated with the ARCore poses, so no COLMAP run is needed.
"""
from collections import deque

import cv2
import numpy as np

_GL_TO_CV = np.diag([1.0, -1.0, -1.0])


class SeedPoints:
    def __init__(self, K: np.ndarray, analysis_scale: float, gaps=(2, 5), max_points=200_000):
        self.K = K                      # intrinsics at the analysis resolution
        self.scale = analysis_scale
        self.gaps = gaps
        self.max_points = max_points
        self.sift = cv2.SIFT_create(2000)
        self.matcher = cv2.BFMatcher()
        self.recent = deque(maxlen=max(gaps))
        self.xyz, self.rgb = [], []

    def add(self, frame_bgr: np.ndarray, c2w: np.ndarray):
        img = frame_bgr
        if self.scale < 1.0:
            img = cv2.resize(img, None, fx=self.scale, fy=self.scale, interpolation=cv2.INTER_AREA)
        kps, desc = self.sift.detectAndCompute(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY), None)
        R = (c2w[:3, :3] @ _GL_TO_CV).T          # world -> OpenCV camera
        t = -R @ c2w[:3, 3]
        cur = {"pts": np.float32([k.pt for k in kps]) if kps else np.zeros((0, 2), np.float32),
               "desc": desc, "R": R, "t": t, "C": c2w[:3, 3], "img": img}
        if desc is not None and len(kps) >= 30:
            for gap in self.gaps:
                if len(self.recent) >= gap:
                    self._triangulate(self.recent[-gap], cur)
        self.recent.append(cur)

    def _triangulate(self, a, b):
        if a["desc"] is None or len(a["pts"]) < 30:
            return
        good = [m for m, m2 in (p for p in self.matcher.knnMatch(a["desc"], b["desc"], k=2) if len(p) == 2)
                if m.distance < 0.75 * m2.distance]
        if len(good) < 20:
            return
        pa = a["pts"][[m.queryIdx for m in good]]
        pb = b["pts"][[m.trainIdx for m in good]]
        Pa = self.K @ np.c_[a["R"], a["t"]]
        Pb = self.K @ np.c_[b["R"], b["t"]]
        X = cv2.triangulatePoints(Pa, Pb, pa.T.astype(np.float64), pb.T.astype(np.float64))
        X = (X[:3] / np.where(np.abs(X[3]) < 1e-12, 1e-12, X[3])).T

        keep = np.ones(len(X), bool)
        for cam, px in ((a, pa), (b, pb)):
            Xc = X @ cam["R"].T + cam["t"]
            z = Xc[:, 2]
            proj = Xc[:, :2] / np.maximum(z, 1e-9)[:, None] * [self.K[0, 0], self.K[1, 1]] + self.K[:2, 2]
            keep &= (z > 0.1) & (z < 20.0) & (np.linalg.norm(proj - px, axis=1) < 1.5)
        ra, rb = X - a["C"], X - b["C"]
        cos = np.sum(ra * rb, 1) / (np.linalg.norm(ra, axis=1) * np.linalg.norm(rb, axis=1) + 1e-12)
        keep &= cos < np.cos(np.radians(1.5))   # enough parallax for a stable depth
        if not keep.any():
            return
        h, w = b["img"].shape[:2]
        uv = np.clip(pb[keep].round().astype(int), 0, [w - 1, h - 1])
        self.xyz.append(X[keep])
        self.rgb.append(b["img"][uv[:, 1], uv[:, 0], ::-1])  # BGR -> RGB

    def write_ply(self, path) -> int:
        if not self.xyz:
            return 0
        xyz, rgb = np.concatenate(self.xyz), np.concatenate(self.rgb)
        if len(xyz) > self.max_points:
            idx = np.random.default_rng(0).choice(len(xyz), self.max_points, replace=False)
            xyz, rgb = xyz[idx], rgb[idx]
        header = ("ply\nformat ascii 1.0\n"
                  f"element vertex {len(xyz)}\n"
                  "property float x\nproperty float y\nproperty float z\n"
                  "property uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n")
        with open(path, "w", encoding="ascii") as f:
            f.write(header)
            for (x, y, z), (r, g, b) in zip(xyz, rgb):
                f.write(f"{x:.5f} {y:.5f} {z:.5f} {r} {g} {b}\n")
        return len(xyz)
