#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 long/short 两组正交渲染中自动挑选每个模型朝向最正的 front/side/top。

用法:
    python FinalizeTriView.py triview_long triview_short refs_base out_base
"""
import math
import os
import shutil
import sys

from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from MakeTriViewSheets import iou, mask  # noqa: E402


def aspect(path):
    m = mask(Image.open(path))
    px = m.load()
    xs, ys = [], []
    for y in range(m.size[1]):
        for x in range(m.size[0]):
            if px[x, y] > 32:
                xs.append(x)
                ys.append(y)
    if not xs:
        return 0.0
    return (max(xs) - min(xs) + 1) / (max(ys) - min(ys) + 1)


def main():
    long_base, short_base, refs_base, out_base = sys.argv[1:5]
    dirs = sorted(
        d for d in os.listdir(long_base)
        if os.path.isdir(os.path.join(long_base, d)) and d[:3].isdigit()
    )
    for d in dirs:
        ref = os.path.join(refs_base, d, "front.png")
        ref_asp = aspect(ref) if os.path.exists(ref) else None
        cands = []
        for mode, base in (("long", long_base), ("short", short_base)):
            for view in ("front", "side"):
                p = os.path.join(base, d, f"ortho_{view}.png")
                if os.path.exists(p):
                    cands.append((mode, view, p))
        best = None
        best_score = None
        for mode, view, p in cands:
            a = aspect(p)
            score = abs(math.log(ref_asp / a)) if ref_asp else 0.0
            sim = iou(mask(Image.open(p)), mask(Image.open(ref))) if ref_asp else 0.0
            total = score - 0.25 * sim
            if best_score is None or total < best_score:
                best_score = total
                best = (mode, view, p, a, score, sim)
        mode, view, p, a, score, sim = best
        other = "side" if view == "front" else "front"
        top = os.path.join(long_base if mode == "long" else short_base, d, "ortho_top.png")
        out = os.path.join(out_base, d)
        os.makedirs(out, exist_ok=True)
        for dst, src in (("front.png", p), ("side.png", os.path.join(
                long_base if mode == "long" else short_base, d, f"ortho_{other}.png")),
                         ("top.png", top)):
            shutil.copyfile(src, os.path.join(out, dst))
        imgs = [Image.open(os.path.join(out, n)).convert("RGB")
                for n in ("front.png", "side.png", "top.png")]
        canvas = Image.new("RGB", (1024 * 3, 1024), (255, 255, 255))
        for i, im in enumerate(imgs):
            canvas.paste(im.resize((1024, 1024), Image.LANCZOS), (i * 1024, 0))
        draw = ImageDraw.Draw(canvas)
        for i, label in enumerate(("FRONT", "SIDE", "TOP")):
            draw.rectangle([i * 1024 + 10, 10, i * 1024 + 170, 44], fill=(20, 20, 24))
            draw.text((i * 1024 + 22, 16), label, fill=(255, 255, 255))
        canvas.save(os.path.join(out, "triview.png"))
        print(f"{d}: chosen={mode}/{view} asp={a:.2f} dist={score:.3f} iou={sim:.2f}")
    print("FINALIZE_DONE")


if __name__ == "__main__":
    main()
