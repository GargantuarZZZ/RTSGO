#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按实际占地尺寸对齐所有 AI 模型：Scale = 占地(格*64) / 水平最大跨度。"""
import io
import json
import os
import re
import struct

LIB = r"D:\DEV\RTSarcade\Scripts\Core\UnitModelLibrary.cs"
CFG = r"D:\DEV\RTSarcade\Data\Configs"

KEYS = [
    # Union
    "SCV", "RifleMan", "RocketMan", "Medic", "Biped", "Hp", "BattleCruiser",
    "CommandCenter", "BB", "VF", "SupplyDepot", "Academy", "Starport", "OrbitalControl",
    # Terran
    "Engineer", "Marine", "HeavyInfantry", "CommandVehicle", "Fighter", "MissileVehicle",
    "FortressCore", "FrontlineCamp", "FieldCamp", "ArmorFactory", "Airfield", "Armory", "AlgaeFactory",
    # Nano
    "NanoBehemoth", "NanoTurret", "NanoSniper", "NanoAA", "NanoHarvester",
    "NanoActiveTower", "NanoSmokeTower", "NanoCore",
    # Demon units
    "DemonWorker", "DemonDog", "DemonFlyer", "HeavyTank", "FireDragon", "LavaBanner",
    "FortGuard", "HellLord", "FireTitan", "Banner",
    # Wanderer
    "Builder", "Hunter", "Harvester", "Shepherd", "Tank", "PlasmaCannon",
    # Demon buildings
    "HellCity", "GreatRift", "HeavyWorkshop", "DragonNest", "ManaTower",
    "SoulStone", "LavaAltar", "CurseFortress", "DemonTower",
]


def footprint(key):
    p = os.path.join(CFG, key + ".tres")
    if not os.path.exists(p):
        return None
    with io.open(p, "r", encoding="utf-8", errors="replace") as f:
        t = f.read()
    m = re.search(r"FootprintTiles\s*=\s*([0-9.]+)", t)
    if m:
        return float(m.group(1)) * 64.0
    w = re.search(r"GridWidth\s*=\s*([0-9.]+)", t)
    h = re.search(r"GridHeight\s*=\s*([0-9.]+)", t)
    if w or h:
        return max(float(w.group(1)) if w else 1.0,
                   float(h.group(1)) if h else 1.0) * 64.0
    return None


DEFAULT_TILES = {
    "NanoBehemoth": 4.0,
}


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
    mins, maxs = None, None
    for node in j.get("nodes", []):
        if "mesh" not in node:
            continue
        acc = j["accessors"][j["meshes"][node["mesh"]]["primitives"][0]["attributes"]["POSITION"]]
        mn, mx = acc["min"], acc["max"]
        if mins is None:
            mins, maxs = mn[:], mx[:]
        else:
            for i in range(3):
                mins[i] = min(mins[i], mn[i])
                maxs[i] = max(maxs[i], mx[i])
    return mins, maxs


def block_span(text, key):
    marker = f'["{key}"] = new UnitModelInfo'
    start = text.find(marker)
    if start < 0:
        return None
    brace = text.find("{", start)
    depth = 0
    i = brace
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    return brace, i


def main():
    with io.open(LIB, "r", encoding="utf-8") as f:
        text = f.read()
    changed = []
    for key in KEYS:
        span = block_span(text, key)
        if span is None:
            print(f"{key}: NOT FOUND")
            continue
        brace, end = span
        block = text[brace + 1:end]
        mp = re.search(r'ModelPath = "([^"]+)"', block)
        if not mp:
            print(f"{key}: no ModelPath")
            continue
        glb = os.path.join(r"D:\DEV\RTSarcade", mp.group(1)[6:])
        if not os.path.exists(glb):
            print(f"{key}: MISSING {glb}")
            continue
        fp = footprint(key)
        if fp is None:
            fp = DEFAULT_TILES.get(key, 1.0) * 64.0
        mn, mx = glb_bbox(glb)
        span_h = max(mx[0] - mn[0], mx[2] - mn[2])
        scale = fp / span_h
        offset = -mn[1] * scale
        specials = []
        for ln in block.splitlines():
            s = ln.strip()
            if not s or s.startswith("ModelPath") or s.startswith("Scale") or \
                    s.startswith("OffsetY") or s.startswith("YMin") or s.startswith("YMax"):
                continue
            specials.append(s.rstrip(","))
        lines = [
            f'\t\t\t\tModelPath = "{mp.group(1)}",',
            f"\t\t\t\tScale = {scale:.2f}f, OffsetY = {offset:.2f}f,",
            f"\t\t\t\tYMin = {mn[1]:.2f}f, YMax = {mx[1]:.2f}f,",
        ]
        for sp in specials:
            lines.append("\t\t\t\t" + sp + ",")
        new_block = "{\n" + "\n".join(lines) + "\n\t\t\t}"
        text = text[:brace] + new_block + text[end + 1:]
        changed.append((key, scale, offset, mn[1], mx[1], fp))
    with io.open(LIB, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    for key, scale, offset, ymin, ymax, fp in changed:
        print(f"{key}: footprint={fp:.0f} Scale={scale:.2f} OffsetY={offset:.2f} Y={ymin:.2f}..{ymax:.2f}")
    print(f"updated {len(changed)} entries")


if __name__ == "__main__":
    main()
