#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按目标视觉高度（单位）/ 占地格数（建筑）校准巫师 / AI 指挥系统模型。

与既有 Tools/RecalibrateModels.py 同一套算法：
    scale  = target / span
    offset = -ymin * scale
只是数据源换成 wizard_ai_refs_gemini，并且**直接从 ArtRes 里已定稿的模型读**，
这样算出来的数值和仓库里的 GLB 保证一致。

用法:
    python Tools/CalibrateWizardAI.py            # 打印
    python Tools/CalibrateWizardAI.py --json out.json
"""
import argparse
import json
import os
import struct
import sys

REFS = r"D:\DEV\AI3D-Pipeline\outputs\wizard_ai_refs_gemini"
ARTROOT = r"D:\DEV\RTSarcade\ArtRes\models"

# (游戏 ID, 提示词目录序号(1起), 族, mode, target)
# mode: h = 按目标视觉高度定标；f = 按目标水平占地宽度定标；a = 自动取更紧的那个
#
# 为什么要有 "a"：横向展开的物体（飞机、扁平建筑）竖向很薄，用"高度"当基准会算出
# 荒唐的缩放 —— 实测 AIAirFactory spanY 只有 0.373，按高 192 算是 514 倍，
# 一个 3x3 建筑会被拉到 15 格宽；武库鸟同样 242 倍。反过来，人形/竖向建筑用
# "占地宽度"当基准会被压扁。所以两条都算，取缩放更小的那个（= 两条约束都满足）。
#
# 高度基准沿用已定稿的联盟/恶魔批次（RecalibrateModels.py）：
#   工人 48.3 / 普通兵 12.96~16.2 / 载具 75.9 / 大单位 90.24~192
ITEMS = [
    # ---- 巫师 · 单位 (ID, 目录序号, 族, mode, 目标高度, 占地格边长) ----
    ("WizPuppet",        1,  "Wizard",    "a", 48.30, 1),
    ("WizApprentice",    2,  "Wizard",    "a", 41.00, 1),
    ("WizHospitalMage",  3,  "Wizard",    "a", 44.00, 1),
    ("WizBattlePuppet",  4,  "Wizard",    "a", 58.00, 2),
    ("WizBroomRider",    5,  "Wizard",    "a", 52.00, 1),
    ("WizElementMage",   6,  "Wizard",    "a", 54.00, 1),
    ("WizMusician",      7,  "Wizard",    "a", 46.00, 1),
    ("WizStoneGolem",    8,  "Wizard",    "a", 90.24, 2),
    ("WizEarthGolem",    9,  "Wizard",    "a", 90.24, 2),
    # ---- 巫师 · 建筑 ----
    ("WizCity",          10, "Wizard",    "a", 256.0, 4),
    ("WizConcertHall",   11, "Wizard",    "a", 192.0, 3),
    ("WizElementForge",  12, "Wizard",    "a", 192.0, 3),
    ("WizForest",        13, "Wizard",    "a", 128.0, 2),
    ("WizHouse",         14, "Wizard",    "a", 128.0, 2),
    ("WizMageTower",     15, "Wizard",    "a", 192.0, 3),
    ("WizSchool",        16, "Wizard",    "a", 128.0, 2),
    ("WizSpaceLab",      17, "Wizard",    "a", 192.0, 3),
    ("WizStoneCircle",   18, "Wizard",    "a", 192.0, 3),
    ("WizWaterWall",     19, "Wizard",    "a", 192.0, 3),
    # ---- AI 指挥系统 · 单位 ----
    ("AIBaseCar",           20, "AICommand", "a", 75.90, 3),
    ("AIKamikaze",          21, "AICommand", "a", 29.10, 1),
    ("AIQuadDrone",         22, "AICommand", "a", 31.20, 1),
    ("AIAirSuperiority",    23, "AICommand", "a", 31.20, 1),
    ("AIInterceptor",       24, "AICommand", "a", 41.60, 2),
    ("AIElectronicWarfare", 25, "AICommand", "a", 41.60, 1),
    ("AIBomber",            26, "AICommand", "a", 44.00, 2),
    ("AIArsenalBird",       27, "AICommand", "a", 90.24, 4),
    # ---- AI 指挥系统 · 建筑 ----
    ("AIAirFactory",     28, "AICommand", "a", 192.0, 3),
    ("AICore",           29, "AICommand", "a", 256.0, 4),
    ("AIGoldStation",    30, "AICommand", "a", 128.0, 2),
    ("AIHeavyAirfield",  31, "AICommand", "a", 256.0, 4),
    ("AILightFactory",   32, "AICommand", "a", 192.0, 3),
    ("AIRelay",          33, "AICommand", "a", 128.0, 2),
    ("AISkyNetCore",     34, "AICommand", "a", 128.0, 2),
]


def ref_dirs():
    return sorted(
        d for d in os.listdir(REFS)
        if os.path.isdir(os.path.join(REFS, d)) and d[:3].isdigit()
    )


def glb_bbox(path):
    """读 GLB 里 POSITION accessor 的 min/max（不解码网格，最快）。"""
    with open(path, "rb") as f:
        data = f.read()
    pos, j = 12, None
    while pos + 8 <= len(data):
        clen, ctype = struct.unpack_from("<II", data, pos)
        chunk = data[pos + 8:pos + 8 + clen]
        if ctype == 0x4E4F534A:
            j = json.loads(chunk)
        pos += 8 + clen
    mins = maxs = None
    for node in j["nodes"]:
        if "mesh" not in node:
            continue
        prim = j["meshes"][node["mesh"]]["primitives"][0]
        acc = j["accessors"][prim["attributes"]["POSITION"]]
        mn, mx = acc["min"], acc["max"]
        if mins is None:
            mins, maxs = mn[:], mx[:]
        else:
            for i in range(3):
                mins[i] = min(mins[i], mn[i])
                maxs[i] = max(maxs[i], mx[i])
    return mins, maxs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", default="", help="把结果写到这个 JSON")
    args = ap.parse_args()

    dirs = ref_dirs()
    results = []
    for uid, idx, race, mode, target, tiles in ITEMS:
        if idx > len(dirs):
            print(f"{uid}: 缺目录 idx={idx}")
            continue
        src = os.path.join(REFS, dirs[idx - 1], "textured_mesh.glb")
        if not os.path.exists(src):
            print(f"{uid}: 缺 {src}")
            continue
        mn, mx = glb_bbox(src)
        ymin, ymax = mn[1], mx[1]
        span_y = ymax - ymin
        span_h = max(mx[0] - mn[0], mx[2] - mn[2])

        scale_h = target / span_y if span_y > 1e-6 else float("inf")
        scale_f = (tiles * 64.0) / span_h if span_h > 1e-6 else float("inf")
        if mode == "h":
            scale, used = scale_h, "h"
        elif mode == "f":
            scale, used = scale_f, "f"
        else:  # 自动：取更紧的约束，两个目标都不超
            scale, used = (scale_h, "h") if scale_h <= scale_f else (scale_f, "f")

        offset = -ymin * scale
        results.append({
            "id": uid, "race": race, "dir": dirs[idx - 1], "mode": used,
            "Scale": round(scale, 2), "OffsetY": round(offset, 2),
            "YMin": round(ymin, 3), "YMax": round(ymax, 3),
            "spanY": round(span_y, 3), "spanH": round(span_h, 3),
            "target": target, "tiles": tiles,
            "visualH": round(span_y * scale, 1),
            "visualW": round(span_h * scale, 1),
        })
        print(f'{uid:<22} {used}  Scale={scale:>8.2f}  OffsetY={offset:>7.2f}  '
              f'高={span_y * scale:>6.1f} 宽={span_h * scale:>6.1f}  (target高={target})')

    print(f"\n共 {len(results)} 个")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(results, f, ensure_ascii=False, indent=1)
        print("写出", args.json)


if __name__ == "__main__":
    main()
