#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把现成的部分视图（front/back/left/right，可缺）喂给本地 Hunyuan3D-2mv 做多视图重建。

用法:
    python RunHunyuanFromViews.py refs_base_dir [--textured] [--octree 64]
        [--steps 5] [--guidance 5.0] [--chunks 8000] [--no-shutdown]

输出写到每个参考图目录下的 white_mesh.glb / textured_mesh.glb。
"""
import argparse
import json
import os
import re
import shutil
import sys

sys.path.insert(0, r"D:\DEV\AI3D-Pipeline")

from PIL import Image  # noqa: E402

from hy3d import HunyuanClient  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description="从现成四视图跑 Hunyuan 多视图重建")
    ap.add_argument("refs_base")
    ap.add_argument("--textured", action="store_true")
    ap.add_argument("--octree", type=int, default=64)
    ap.add_argument("--steps", type=int, default=5)
    ap.add_argument("--guidance", type=float, default=5.0)
    ap.add_argument("--chunks", type=int, default=8000)
    ap.add_argument("--seed", type=int, default=777)
    ap.add_argument("--only", type=int, default=0, help="只处理第 N 个目录（1 起）")
    ap.add_argument("--no-shutdown", action="store_true")
    args = ap.parse_args()

    dirs = sorted(
        d for d in os.listdir(args.refs_base)
        if os.path.isdir(os.path.join(args.refs_base, d)) and re.match(r"^\d{3}_", d)
    )
    if args.only > 0:
        dirs = dirs[args.only - 1:args.only]
    if not dirs:
        print("refs_base 里没有编号目录")
        sys.exit(1)

    h3d = HunyuanClient()
    results = []
    try:
        for i, d in enumerate(dirs, 1):
            dpath = os.path.join(args.refs_base, d)
            views = {}
            for v in ("front", "back", "left", "right"):
                vp = os.path.join(dpath, f"{v}.png")
                if os.path.exists(vp):
                    views[v] = Image.open(vp).convert("RGB")
                else:
                    print(f"  {v}.png 缺失，该槽位留空")
            if "front" not in views:
                print(f"[{i}/{len(dirs)}] 缺少 front.png: {dpath}")
                sys.exit(1)
            print(f"[{i}/{len(dirs)}] {d} 开始重建 (textured={args.textured})")
            glb = h3d.generate(
                views, seed=args.seed + i - 1, steps=args.steps,
                guidance=args.guidance, octree=args.octree,
                chunks=args.chunks, textured=args.textured,
            )
            name = "textured_mesh.glb" if args.textured else "white_mesh.glb"
            dst = os.path.join(dpath, name)
            shutil.copyfile(glb, dst)
            meta = {
                "prompt_dir": d,
                "seed": args.seed + i - 1,
                "textured": args.textured,
                "steps": args.steps,
                "guidance_scale": args.guidance,
                "octree_resolution": args.octree,
                "views": list(views.keys()),
                "output": dst,
            }
            with open(os.path.join(dpath, "meta_mv.json"), "w", encoding="utf-8") as f:
                json.dump(meta, f, ensure_ascii=False, indent=2)
            results.append({"dir": d, "output": dst})
            print(f"[{i}/{len(dirs)}] 完成: {dst}")
    finally:
        if not args.no_shutdown:
            h3d.shutdown()
            print("Hunyuan3D server stopped (VRAM released).")
    print(json.dumps(results, ensure_ascii=False, indent=2))
    print("HUNYUAN_BATCH_DONE")


if __name__ == "__main__":
    main()
