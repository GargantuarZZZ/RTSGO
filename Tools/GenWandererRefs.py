#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""阵营参考图：只生成正面 + 右面两张参考图，背面/左侧留空。

风格锚点 = 每个阵营独立的固定文本段（--anchor 指定文件），自动拼到每条 prompt，不使用参考图锁风格。
用法:
    python GenWandererRefs.py prompts.txt out_dir [base_seed]
        [--only N] [--force] [--right-only] [--subject creature] --anchor Tools/StyleAnchors/Demon.txt
"""
import argparse
import base64
import io
import os
import re
import sys

import requests
from PIL import Image

API_BASE = "https://generativelanguage.googleapis.com/v1beta/models"

def slugify(text, limit=48):
    s = re.sub(r"[^\w\u4e00-\u9fff]+", "_", text.strip()).strip("_")
    return s[:limit] or "model"


def encode_png(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def call_gemini(prompt, key, model, ref_img=None):
    url = f"{API_BASE}/{model}:generateContent"
    parts = []
    if ref_img is not None:
        parts.append({"inlineData": {"mimeType": "image/png", "data": encode_png(ref_img)}})
    parts.append({"text": prompt})
    payload = {
        "contents": [{"parts": parts}],
        "generationConfig": {"responseModalities": ["TEXT", "IMAGE"]},
    }
    headers = {
        "x-goog-api-key": key,
        "Content-Type": "application/json",
    }
    r = requests.post(url, headers=headers, json=payload, timeout=300)
    if r.status_code != 200:
        raise RuntimeError(f"Gemini HTTP {r.status_code}: {r.text[:300]}")
    data = r.json()
    candidates = data.get("candidates") or []
    if not candidates:
        raise RuntimeError(f"Gemini 没有返回候选: {str(data)[:300]}")
    for part in candidates[0].get("content", {}).get("parts", []):
        inline = part.get("inlineData")
        if inline and inline.get("data"):
            raw = base64.b64decode(inline["data"])
            return Image.open(io.BytesIO(raw)).convert("RGB")
    raise RuntimeError(f"Gemini 响应里没有图片: {str(candidates[0])[:300]}")


def main():
    ap = argparse.ArgumentParser(description="阵营 Gemini 正面+右面参考图（文本风格锚点）")
    ap.add_argument("prompts_file")
    ap.add_argument("out_dir")
    ap.add_argument("base_seed", type=int, nargs="?", default=777)
    ap.add_argument("--only", type=int, default=0)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--right-only", action="store_true",
                    help="只用现有 front.png 重新生成 right.png")
    ap.add_argument("--subject", type=str, default="creature",
                    help="主体称呼，用于右视图描述（robot/creature/character 等）")
    ap.add_argument("--anchor", type=str, required=True,
                    help="阵营风格锚点文本文件（每个阵营一个，例如 Tools/StyleAnchors/Demon.txt）")
    args = ap.parse_args()

    key = os.environ.get("GOOGLE_API_KEY", "")
    if not key:
        print("GOOGLE_API_KEY 未设置")
        sys.exit(1)

    with open(args.prompts_file, "r", encoding="utf-8") as f:
        prompts = [ln.strip() for ln in f if ln.strip() and not ln.startswith("#")]
    if args.only > 0:
        prompts = prompts[args.only - 1:args.only]

    models = [os.environ.get("GEMINI_IMAGE_MODEL", "gemini-2.5-flash-image")]
    os.makedirs(args.out_dir, exist_ok=True)
    subj = args.subject
    with open(args.anchor, "r", encoding="utf-8") as f:
        style_anchor = f.read().strip()

    for i, p in enumerate(prompts):
        seed = args.base_seed + i
        d = os.path.join(args.out_dir, f"{i + 1:03d}_{slugify(p)}")
        os.makedirs(d, exist_ok=True)
        front_path = os.path.join(d, "front.png")
        right_path = os.path.join(d, "right.png")
        print(f"[{i + 1}/{len(prompts)}] {d}")

        front = None
        if args.right_only:
            if not os.path.exists(front_path):
                print(f"  !! 缺少 front.png，无法重出右视图: {front_path}")
                continue
            front = Image.open(front_path).convert("RGB")
            print("  复用现有 front.png")
        else:
            if os.path.exists(front_path) and not args.force:
                print("  front.png 已存在，跳过")
                front = Image.open(front_path).convert("RGB")
            else:
                full = p.rstrip(".,，。") + ", " + style_anchor
                for model in models:
                    try:
                        front = call_gemini(full, key, model)
                        front.save(front_path)
                        print(f"  saved front.png (seed={seed})")
                        break
                    except Exception as e:  # noqa: BLE001
                        print(f"  front {model} 失败: {type(e).__name__}: {str(e)[:160]}")
                if front is None:
                    print("  !! front 生成失败")

        if front is None:
            continue

        if os.path.exists(right_path) and not args.force and not args.right_only:
            print("  right.png 已存在，跳过")
            continue
        side_anchor = style_anchor.replace(
            "orthographic front view", "orthographic right side view"
        )
        full = (
            f"The attached image is the EXACT SUBJECT reference: the same {subj} in front view. "
            f"Rotate the {subj} in 3D 90 degrees around its vertical axis to its RIGHT side, "
            f"as if the camera walked around the {subj} and now stands on the {subj}'s right side "
            f"looking at its right side. In a RIGHT side view, the {subj}'s FRONT must point to "
            f"the RIGHT edge of the image and its BACK points to the LEFT edge. Keep identical "
            f"proportions, colors, materials, pose and style; do NOT mirror or flip the image "
            f"horizontally, do NOT show the left side. " + side_anchor
        )
        ok = False
        for model in models:
            try:
                right = call_gemini(full, key, model, ref_img=front)
                right.save(right_path)
                print("  saved right.png")
                ok = True
                break
            except Exception as e:  # noqa: BLE001
                print(f"  right {model} 失败: {type(e).__name__}: {str(e)[:160]}")
        if not ok:
            print("  !! right 生成失败")

    print("REFS_DONE")


if __name__ == "__main__":
    main()
