#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""批量校准新 GLB：按实体目标高度（或占地宽度）计算 Scale/OffsetY/YMin/YMax。"""
import json
import os
import struct
import sys

BASES = {
    "union": r"D:\DEV\AI3D-Pipeline\outputs\union_refs_gemini",
    "terran": r"D:\DEV\AI3D-Pipeline\outputs\terran_refs_gemini",
    "nano": r"D:\DEV\AI3D-Pipeline\outputs\nano_refs_gemini",
    "demon_b": r"D:\DEV\AI3D-Pipeline\outputs\demon_buildings_refs_gemini",
}

# (base, index(1起), entity, mode, target)
# mode: h = 目标视觉高度；f = 目标水平占地宽度
ITEMS = [
    ("union", 1, "SCV", "h", 48.3),
    ("union", 2, "RifleMan", "h", 12.96),
    ("union", 3, "RocketMan", "h", 12.96),
    ("union", 4, "Medic", "h", 12.96),
    ("union", 5, "Biped", "h", 90.24),
    ("union", 6, "Hp", "h", 41.6),
    ("union", 7, "BattleCruiser", "h", 29.1),
    ("union", 8, "CommandCenter", "h", 141.7),
    ("union", 9, "BB", "h", 128.0),
    ("union", 10, "VF", "h", 192.0),
    ("union", 11, "SupplyDepot", "h", 74.7),
    ("union", 12, "Academy", "h", 128.0),
    ("union", 13, "Starport", "h", 192.0),
    ("union", 14, "OrbitalControl", "h", 216.8),
    ("terran", 1, "Engineer", "h", 48.3),
    ("terran", 2, "Marine", "h", 12.96),
    ("terran", 3, "HeavyInfantry", "h", 16.2),
    ("terran", 4, "CommandVehicle", "h", 75.9),
    ("terran", 5, "Fighter", "h", 31.2),
    ("terran", 6, "MissileVehicle", "h", 91.8),
    ("terran", 7, "FortressCore", "h", 141.7),
    ("terran", 8, "FrontlineCamp", "h", 128.0),
    ("terran", 9, "FieldCamp", "h", 128.0),
    ("terran", 10, "ArmorFactory", "h", 192.0),
    ("terran", 11, "Airfield", "h", 192.0),
    ("terran", 12, "Armory", "h", 128.0),
    ("terran", 13, "AlgaeFactory", "h", 144.0),
    ("nano", 1, "NanoBehemoth", "h", 185.4),
    ("nano", 2, "NanoTurret", "f", 112.0),
    ("nano", 3, "NanoSniper", "f", 112.0),
    ("nano", 4, "NanoAA", "f", 112.0),
    ("nano", 5, "NanoHarvester", "h", 158.4),
    ("nano", 6, "NanoActiveTower", "f", 112.0),
    ("nano", 7, "NanoSmokeTower", "f", 112.0),
    ("nano", 8, "NanoCore", "h", 158.4),
    ("demon_b", 1, "HellCity", "h", 146.9),
    ("demon_b", 2, "GreatRift", "h", 144.0),
    ("demon_b", 3, "HeavyWorkshop", "h", 192.0),
    ("demon_b", 4, "DragonNest", "h", 160.0),
    ("demon_b", 5, "ManaTower", "f", 80.0),
    ("demon_b", 6, "SoulStone", "h", 50.0),
    ("demon_b", 7, "LavaAltar", "h", 135.3),
    ("demon_b", 8, "CurseFortress", "h", 168.8),
    ("demon_b", 9, "DemonTower", "h", 160.0),
]


def glb_bbox(path):
    with open(path, "rb") as f:
        data = f.read()
    pos = 12
    j = None
    while pos + 8 <= len(data):
        clen, ctype = struct.unpack_from("<II", data, pos)
        chunk = data[pos + 8:pos + 8 + clen]
        if ctype == 0x4E4F534A:
            j = json.loads(chunk)
        pos += 8 + clen
    nodes = j["nodes"]
    meshes = j["meshes"]
    accessors = j["accessors"]
    mins, maxs = None, None
    for node in nodes:
        if "mesh" not in node:
            continue
        prim = meshes[node["mesh"]]["primitives"][0]
        acc = accessors[prim["attributes"]["POSITION"]]
        mn = acc["min"]
        mx = acc["max"]
        if mins is None:
            mins, maxs = mn[:], mx[:]
        else:
            for i in range(3):
                mins[i] = min(mins[i], mn[i])
                maxs[i] = max(maxs[i], mx[i])
    return mins, maxs


def main():
    for base_key, idx, entity, mode, target in ITEMS:
        base = BASES[base_key]
        dirs = sorted(
            d for d in os.listdir(base)
            if os.path.isdir(os.path.join(base, d)) and d[:3].isdigit()
        )
        if idx > len(dirs):
            print(f"{entity}: MISSING dir idx {idx}")
            continue
        glb = os.path.join(base, dirs[idx - 1], "textured_mesh.glb")
        if not os.path.exists(glb):
            print(f"{entity}: MISSING {glb}")
            continue
        mn, mx = glb_bbox(glb)
        ymin, ymax = mn[1], mx[1]
        span_y = ymax - ymin
        if mode == "h":
            scale = target / span_y
        else:
            span_h = max(mx[0] - mn[0], mx[2] - mn[2])
            scale = target / span_h
        offset = -ymin * scale
        print(f"{entity}: Scale={scale:.2f}, OffsetY={offset:.2f}, "
              f"YMin={ymin:.2f}, YMax={ymax:.2f}  (spanY={span_y:.2f})")
    print("RECAL_DONE")


if __name__ == "__main__":
    main()
