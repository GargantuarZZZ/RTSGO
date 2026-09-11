#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""统计 GLB 网格的顶点/三角面数量（纯解析，无需渲染）。用法：python GlbStats.py <glb...>"""
import json
import struct
import sys


def read_glb(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"glTF":
        raise ValueError("not a GLB: " + path)
    pos = 12
    json_data = None
    while pos + 8 <= len(data):
        clen, ctype = struct.unpack_from("<II", data, pos)
        chunk = data[pos + 8 : pos + 8 + clen]
        if ctype == 0x4E4F534A:
            json_data = json.loads(chunk.decode("utf-8"))
        pos += 8 + clen
    return json_data


def glb_stats(path):
    j = read_glb(path)
    accessors = j.get("accessors", [])
    verts = 0
    tris = 0
    for mesh in j.get("meshes", []):
        for prim in mesh.get("primitives", []):
            pos_acc = prim.get("attributes", {}).get("POSITION")
            if pos_acc is None:
                continue
            vcount = accessors[pos_acc]["count"]
            verts += vcount
            idx_acc = prim.get("indices")
            if idx_acc is not None:
                tris += accessors[idx_acc]["count"] // 3
            else:
                tris += vcount // 3
    return verts, tris


def main():
    if len(sys.argv) < 2:
        print("usage: python GlbStats.py <glb...>")
        return 1
    for path in sys.argv[1:]:
        try:
            v, t = glb_stats(path)
            print(f"{v}\t{t}\t{path}")
        except Exception as e:
            print(f"-\t-\t{path} ({e})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
