#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用 pymeshlab 把 GLB 减面到指定三角面数以内（保留纹理/UV/法线）。

用法:
    python DecimateGlb.py <glb...> [--target 9500] [--in-place]
"""
import argparse
import os
import shutil
import sys
import tempfile

import numpy as np
import pymeshlab
import trimesh
from trimesh.visual import TextureVisuals


def face_count(ms):
    return ms.current_mesh().face_number()


def decimate(src, dst, target, aggressive=False):
    orig = trimesh.load(src, process=False)
    if isinstance(orig, trimesh.Scene):
        geoms = [g for g in orig.geometry.values() if isinstance(g, trimesh.Trimesh)]
        orig = geoms[0] if geoms else None
    if orig is None:
        raise RuntimeError(f"no Trimesh geometry in {src}")
    material = orig.visual.material
    ms = pymeshlab.MeshSet()
    ms.load_new_mesh(src)
    faces = face_count(ms)
    if faces <= target:
        shutil.copyfile(src, dst)
        return faces, faces
    if aggressive:
        ms.meshing_decimation_quadric_edge_collapse(
            targetfacenum=target,
            qualitythr=0.5,
            preserveboundary=False,
            preservenormal=True,
            preservetopology=False,
        )
    else:
        try:
            ms.meshing_decimation_quadric_edge_collapse_with_texture(
                targetfacenum=target,
                qualitythr=0.3,
                preserveboundary=True,
                preservenormal=True,
                preservetexture=True,
            )
        except Exception:  # noqa: BLE001
            ms.meshing_decimation_quadric_edge_collapse(
                targetfacenum=target,
                qualitythr=0.3,
                preserveboundary=True,
                preservenormal=True,
            )
    verts = ms.current_mesh().vertex_matrix()
    tris = ms.current_mesh().face_matrix()
    try:
        uv = ms.current_mesh().wedge_tex_coord_matrix()
    except Exception:  # noqa: BLE001
        uv = None
    if uv is not None and len(uv) == len(tris) * 3:
        verts2 = verts[tris.reshape(-1)]
        tris2 = np.arange(len(verts2)).reshape(-1, 3)
        visual = TextureVisuals(uv=uv, material=material)
        mesh = trimesh.Trimesh(vertices=verts2, faces=tris2, process=False, visual=visual)
    else:
        mesh = trimesh.Trimesh(vertices=verts, faces=tris, process=False)
    mesh.export(dst, file_type="glb", include_normals=True)
    return faces, len(mesh.faces)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("glbs", nargs="+")
    ap.add_argument("--target", type=int, default=9500)
    ap.add_argument("--in-place", action="store_true")
    ap.add_argument("--aggressive", action="store_true")
    args = ap.parse_args()
    for src in args.glbs:
        if args.in_place:
            fd, tmp = tempfile.mkstemp(suffix=".glb", dir=os.path.dirname(src))
            os.close(fd)
            before, after = decimate(src, tmp, args.target, aggressive=args.aggressive)
            os.replace(tmp, src)
        else:
            dst = src.replace(".glb", f"_dec{args.target}.glb")
            before, after = decimate(src, dst, args.target, aggressive=args.aggressive)
        print(f"{before}\t{after}\t{src}")
    print("DECIMATE_DONE")


if __name__ == "__main__":
    main()
