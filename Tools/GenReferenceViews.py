#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""只生成 2D 参考图（不跑 3D 重建），用于 prompt/风格评审。
用法: python GenReferenceViews.py prompts.txt out_dir [base_seed]"""
import os
import re
import sys

sys.path.insert(0, r"D:\DEV\AI3D-Pipeline")
from t2i import ViewGenerator


def slugify(text, limit=48):
    s = re.sub(r"[^\w\u4e00-\u9fff]+", "_", text.strip()).strip("_")
    return s[:limit] or "model"


def main():
    prompts_file = sys.argv[1]
    out_root = sys.argv[2]
    base_seed = int(sys.argv[3]) if len(sys.argv) > 3 else 777

    with open(prompts_file, "r", encoding="utf-8") as f:
        prompts = [line.strip() for line in f if line.strip() and not line.startswith("#")]

    os.makedirs(out_root, exist_ok=True)
    vg = ViewGenerator()

    for i, p in enumerate(prompts):
        seed = base_seed + i
        print(f"[{i + 1}/{len(prompts)}] seed={seed}")
        imgs = vg.generate_views(p, views=("front",), seed=seed)
        d = os.path.join(out_root, f"{i + 1:03d}_{slugify(p)}")
        os.makedirs(d, exist_ok=True)
        imgs["front"].save(os.path.join(d, "front.png"))
        print(f"saved {d}/front.png")

    print("REF_VIEWS_DONE")


if __name__ == "__main__":
    main()
