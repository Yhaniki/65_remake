# -*- coding: utf-8 -*-
"""Headless render of the WOMAN SDO avatar, replicating avatar_viewer.py's
outfit-preview assembly + skinning, to PNG.  CPU rasterizer (no moderngl needed).

Renders the same draw batches that avatar_viewer feeds to render_msh_gpu_skinned,
but rasterizes them with a small NumPy software renderer (Y-up, same camera math
as bms_sdo.msh_renderer / msh_renderer_gl).

Outputs:
  H:/65_remake/ref_avatar.png            (skin mode = msh_invbind, viewer default)
  H:/65_remake/ref_avatar_retarget.png   (skin mode = retarget)
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

TOOLS = Path(r"H:\bms\tools")
sys.path.insert(0, str(TOOLS))

import bms_sdo.mesh_skin as _mesh_skin_state  # noqa: E402
from bms_sdo.mesh_skin import (  # noqa: E402
    build_textured_skin_body,
    set_force_skin_mode,
    skin_body_gpu_draws,
)
from bms_sdo.mot_core import MotionClip  # noqa: E402
from bms_sdo.mot_player import (  # noqa: E402
    CONNECTIONS,
    build_skeleton,
    evaluate_pose,
    guess_hrc,
    read_hrc,
    read_mot,
)

AVATAR = Path(r"H:\65_remake\assets\sdox_offline\Extracted\AVATAR")
HRC = AVATAR / "FEMALE.HRC"
MOT = Path(r"H:\65_remake\assets\sdox_offline\Extracted\MOTION\WREST0056.MOT")

# Default WOMAN body parts (same set as avatar_viewer's DEFAULT_OUTFIT_SLOT_STEMS).
PART_STEMS = [
    "900007_WOMAN_FACE",
    "900017_WOMAN_HAIR",
    "900018_WOMAN_COAT",
    "900019_WOMAN_PANT",
    "900020_WOMAN_SHOES",
    "900011_WOMAN_HAND",
]


# ---- camera (matches bms_sdo.msh_renderer._make_view_matrix) ----
def make_view_matrix(yaw_deg: float, pitch_deg: float) -> np.ndarray:
    ay = np.deg2rad(yaw_deg)
    ax = np.deg2rad(pitch_deg)
    cy, sy = np.cos(ay), np.sin(ay)
    cx, sx = np.cos(ax), np.sin(ax)
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]], dtype=np.float32)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]], dtype=np.float32)
    return Rx @ Ry


def compute_vertex_normals(positions: np.ndarray, indices: np.ndarray) -> np.ndarray:
    v0 = positions[indices[:, 0]]
    v1 = positions[indices[:, 1]]
    v2 = positions[indices[:, 2]]
    face_n = np.cross(v1 - v0, v2 - v0)
    norms = np.linalg.norm(face_n, axis=1, keepdims=True)
    face_n = face_n / np.maximum(norms, 1e-8)
    n = np.zeros_like(positions)
    for i in range(3):
        np.add.at(n, indices[:, i], face_n)
    norms = np.linalg.norm(n, axis=1, keepdims=True)
    return n / np.maximum(norms, 1e-8)


def rasterize(draws, *, size, yaw_deg, pitch_deg, center, radius,
              bg=(32, 32, 38), light_dir=(0.4, 0.6, 0.7), margin=0.9):
    """Software rasterizer over draw tuples (pos, uvs, idx, tex)."""
    H = W = int(size)
    fb = np.zeros((H, W, 3), dtype=np.uint8)
    fb[..., 0], fb[..., 1], fb[..., 2] = bg
    zb = np.full((H, W), np.inf, dtype=np.float32)

    view = make_view_matrix(yaw_deg, pitch_deg)
    light = np.asarray(light_dir, dtype=np.float32)
    light = light / (np.linalg.norm(light) + 1e-8)

    for pos, uvs, idx_rows, tex_payload in draws:
        pos = np.asarray(pos, dtype=np.float32)
        uvs = np.asarray(uvs, dtype=np.float32)
        idx = np.asarray(idx_rows, dtype=np.int32).reshape(-1, 3)
        if idx.size == 0 or pos.size == 0:
            continue
        # texture: tex or [tex0, tex1]; use tex0
        if isinstance(tex_payload, (list, tuple)):
            tex = tex_payload[0]
        else:
            tex = tex_payload
        if tex is None:
            continue
        tex = np.asarray(tex, dtype=np.uint8)
        TH, TW = tex.shape[:2]

        p = (pos - center) / radius
        normals = compute_vertex_normals(p, idx)
        pv = p @ view.T
        nv = normals @ view.T

        sx = (pv[:, 0] * margin * 0.5 + 0.5) * W
        sy = (1.0 - (pv[:, 1] * margin * 0.5 + 0.5)) * H
        sz = pv[:, 2]

        for tri in idx:
            i0, i1, i2 = int(tri[0]), int(tri[1]), int(tri[2])
            x0, y0 = sx[i0], sy[i0]
            x1, y1 = sx[i1], sy[i1]
            x2, y2 = sx[i2], sy[i2]
            area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
            if abs(area) < 1e-3:
                continue
            xmin = max(0, int(np.floor(min(x0, x1, x2))))
            xmax = min(W - 1, int(np.ceil(max(x0, x1, x2))))
            ymin = max(0, int(np.floor(min(y0, y1, y2))))
            ymax = min(H - 1, int(np.ceil(max(y0, y1, y2))))
            if xmin > xmax or ymin > ymax:
                continue
            xs = np.arange(xmin, xmax + 1, dtype=np.float32)
            ys = np.arange(ymin, ymax + 1, dtype=np.float32)
            X, Y = np.meshgrid(xs + 0.5, ys + 0.5)
            e0 = (x2 - x1) * (Y - y1) - (y2 - y1) * (X - x1)
            e1 = (x0 - x2) * (Y - y2) - (y0 - y2) * (X - x2)
            e2 = (x1 - x0) * (Y - y0) - (y1 - y0) * (X - x0)
            w0 = e0 / area
            w1 = e1 / area
            w2 = e2 / area
            inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
            if not inside.any():
                continue
            depth = w0 * sz[i0] + w1 * sz[i1] + w2 * sz[i2]
            cur_z = zb[ymin:ymax + 1, xmin:xmax + 1]
            accept = inside & (depth < cur_z)
            if not accept.any():
                continue
            u = w0 * uvs[i0, 0] + w1 * uvs[i1, 0] + w2 * uvs[i2, 0]
            v = w0 * uvs[i0, 1] + w1 * uvs[i1, 1] + w2 * uvs[i2, 1]
            tx = np.clip((u * (TW - 1)).astype(np.int32), 0, TW - 1)
            ty = np.clip((v * (TH - 1)).astype(np.int32), 0, TH - 1)
            s0 = tex[ty, tx]

            nxv = w0 * nv[i0, 0] + w1 * nv[i1, 0] + w2 * nv[i2, 0]
            nyv = w0 * nv[i0, 1] + w1 * nv[i1, 1] + w2 * nv[i2, 1]
            nzv = w0 * nv[i0, 2] + w1 * nv[i1, 2] + w2 * nv[i2, 2]
            nlen = np.sqrt(nxv * nxv + nyv * nyv + nzv * nzv) + 1e-8
            ndl = np.abs(nxv * light[0] + nyv * light[1] + nzv * light[2]) / nlen
            shade = (ndl * 0.55 + 0.45)[..., None].astype(np.float32)

            rgb = np.clip(s0[..., :3].astype(np.float32) * shade, 0, 255).astype(np.uint8)
            fa = s0[..., 3].astype(np.float32) / 255.0
            eps = 1.0 / 255.0
            blend_mask = accept & (fa >= eps) & (depth < cur_z)
            if not blend_mask.any():
                continue
            cur_fb = fb[ymin:ymax + 1, xmin:xmax + 1]
            a = fa[..., None]
            blended = np.clip(rgb.astype(np.float32) * a +
                              cur_fb.astype(np.float32) * (1.0 - a), 0, 255).astype(np.uint8)
            fb[ymin:ymax + 1, xmin:xmax + 1] = np.where(blend_mask[..., None], blended, cur_fb)
            zb[ymin:ymax + 1, xmin:xmax + 1] = np.where(blend_mask, depth, cur_z)

    return Image.fromarray(fb, mode="RGB")


def build_layers(clip):
    layers = []
    for stem in PART_STEMS:
        msh = AVATAR / f"{stem}.MSH"
        dds = AVATAR / f"{stem}.DDS"
        dds_list = [dds] if dds.is_file() else []
        layer = build_textured_skin_body([msh], dds_list, clip.skeleton)
        if layer is None:
            print(f"  [warn] layer build failed: {stem}")
            continue
        layers.append((stem, layer))
    return layers


def collect_draws(layers, clip):
    all_draws = []
    for _stem, layer in layers:
        d = skin_body_gpu_draws(layer, clip.skeleton, clip.nodes_by_id, 0.0)
        all_draws.extend(d)
    return all_draws


def bbox_of(draws):
    pts = [np.asarray(d[0], dtype=np.float32) for d in draws if np.asarray(d[0]).size]
    merged = np.concatenate(pts, axis=0)
    mn = merged.min(0)
    mx = merged.max(0)
    center = ((mn + mx) * 0.5).astype(np.float32)
    radius = float(np.linalg.norm(merged - center, axis=1).max())
    if radius < 1e-6:
        radius = 1.0
    return center, radius, mn, mx, merged


def apply_skin_mode(mode):
    """msh_invbind goes through the public API; 'retarget' is an internal
    per-bone formula not exposed by set_force_skin_mode, so we set the module
    global FORCED_SKIN_MODE directly (same value _compute_skin_mat_for_bone
    consumes). USE_BIND_AS_POSE stays False so the rest MOT pose is applied."""
    if mode == "retarget":
        _mesh_skin_state.FORCED_SKIN_MODE = "retarget"
        _mesh_skin_state.USE_BIND_AS_POSE = False
    else:
        set_force_skin_mode(mode)


def render_mode(clip, mode, out_path, size=720):
    apply_skin_mode(mode)
    layers = build_layers(clip)
    draws = collect_draws(layers, clip)
    center, radius, mn, mx, merged = bbox_of(draws)
    # Front view: yaw=0, pitch=0 -> looking down -Z, Y up, X right (Y-up native).
    img = rasterize(draws, size=size, yaw_deg=0.0, pitch_deg=0.0,
                    center=center, radius=radius)
    img.save(out_path)
    print(f"[OK] {mode} -> {out_path}")
    return center, radius, mn, mx, merged, layers, draws


def report_proportions(mode, center, radius, mn, mx, merged):
    print(f"\n=== proportions ({mode}) ===")
    print(f"  bbox min = {mn}")
    print(f"  bbox max = {mx}")
    print(f"  center   = {center}")
    print(f"  radius   = {radius:.3f}")
    size = mx - mn
    print(f"  size (X,Y,Z) = {size}")
    print(f"  total height (Y span) = {size[1]:.3f}")
    print(f"  width (X span)        = {size[0]:.3f}")
    print(f"  depth (Z span)        = {size[2]:.3f}")


def main():
    print(f"HRC = {HRC}")
    print(f"MOT = {MOT}")
    clip = MotionClip.load(
        str(MOT),
        hrc_path=str(HRC),
        read_mot=read_mot,
        read_hrc=read_hrc,
        build_skeleton=build_skeleton,
        evaluate_pose=evaluate_pose,
        guess_hrc=guess_hrc,
        connections=CONNECTIONS,
    )
    print(f"clip loaded: {clip.total_frames} frames, {len(clip.skeleton)} bones")

    c1, r1, mn1, mx1, mg1, layers1, draws1 = render_mode(
        clip, "msh_invbind", r"H:\65_remake\ref_avatar.png")
    report_proportions("msh_invbind", c1, r1, mn1, mx1, mg1)

    c2, r2, mn2, mx2, mg2, layers2, draws2 = render_mode(
        clip, "retarget", r"H:\65_remake\ref_avatar_retarget.png")
    report_proportions("retarget", c2, r2, mn2, mx2, mg2)

    # Per-part Y extents (msh_invbind) to read head/feet positions.
    print("\n=== per-part Y extents (msh_invbind) ===")
    apply_skin_mode("msh_invbind")
    for stem, layer in layers1:
        d = skin_body_gpu_draws(layer, clip.skeleton, clip.nodes_by_id, 0.0)
        pts = [np.asarray(x[0], dtype=np.float32) for x in d if np.asarray(x[0]).size]
        if not pts:
            continue
        m = np.concatenate(pts, axis=0)
        print(f"  {stem:22s} Ymin={m[:,1].min():8.2f} Ymax={m[:,1].max():8.2f} "
              f"Xmin={m[:,0].min():7.2f} Xmax={m[:,0].max():7.2f} "
              f"Zmin={m[:,2].min():7.2f} Zmax={m[:,2].max():7.2f}")


if __name__ == "__main__":
    main()
