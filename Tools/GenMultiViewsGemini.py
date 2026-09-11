#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用 Gemini 2.5 Flash Image 以主视图为参考生成背面/侧面多视图。

流程：读取已有 front.png -> 以 front 为参考生成 back / right -> 镜像 right 得到 left。
用法:
    python GenMultiViewsGemini.py prompts.txt refs_base_dir [--only N] [--force]
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

DIRECTIONS = {
    "back": (
        "orthographic back view, back view, seen from directly behind, "
        "centered, entire object visible"
    ),
    "right": (
        "orthographic right side view, right side view, "
        "the character's right side facing the camera, centered, entire object visible"
    ),
}


def slugify(text, limit=48):
    s = re.sub(r"[^\w\u4e00-\u9fff]+", "_", text.strip()).strip("_")
    return s[:limit] or "model"


def encode_png(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def call_gemini(prompt, ref_img, key, model):
    url = f"{API_BASE}/{model}:generateContent"
    payload = {
        "contents": [{
            "parts": [
                {"inlineData": {"mimeType": "image/png", "data": encode_png(ref_img)}},
                {"text": prompt},
            ]
        }],
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


def build_prompt(p, view):
    base = p.strip().rstrip(".,，。")
    base = base.replace("orthographic front view", DIRECTIONS[view])
    return (
        "Use the attached reference image as the exact same character. "
        "Keep identical proportions, colors, materials, armor, pose and all details; "
        "only rotate the camera. " + base
    )


def main():
    ap = argparse.ArgumentParser(description="Gemini 多视图生成（front -> back/right/left）")
    ap.add_argument("prompts_file")
    ap.add_argument("refs_base")
    ap.add_argument("--only", type=int, default=0, help="只处理第 N 个目录（1 起）")
    ap.add_argument("--force", action="store_true", help="已存在的视图也重新生成")
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

    dirs = sorted(
        d for d in os.listdir(args.refs_base)
        if os.path.isdir(os.path.join(args.refs_base, d)) and re.match(r"^\d{3}_", d)
    )
    if args.only > 0:
        dirs = dirs[args.only - 1:args.only]

    for i, (d, p) in enumerate(zip(dirs, prompts), 1):
        dpath = os.path.join(args.refs_base, d)
        front_path = os.path.join(dpath, "front.png")
        if not os.path.exists(front_path):
            print(f"[{i}/{len(dirs)}] 缺少 front.png: {dpath}")
            continue
        front = Image.open(front_path).convert("RGB")
        print(f"[{i}/{len(dirs)}] {d}")
        for view in ("back", "right"):
            out_path = os.path.join(dpath, f"{view}.png")
            if os.path.exists(out_path) and not args.force:
                print(f"  {view}.png 已存在，跳过")
                continue
            full = build_prompt(p, view)
            ok = False
            for model in models:
                try:
                    img = call_gemini(full, front, key, model)
                    img.save(out_path)
                    print(f"  saved {view}.png")
                    ok = True
                    break
                except Exception as e:  # noqa: BLE001
                    print(f"  {view} {model} 失败: {type(e).__name__}: {str(e)[:160]}")
            if not ok:
                print(f"  !! {view} 生成失败")
        # 镜像右侧得到左侧（左右对称设计）
        right_path = os.path.join(dpath, "right.png")
        left_path = os.path.join(dpath, "left.png")
        if os.path.exists(right_path) and (not os.path.exists(left_path) or args.force):
            Image.open(right_path).convert("RGB").transpose(Image.FLIP_LEFT_RIGHT).save(left_path)
            print("  left.png 由 right.png 镜像生成")

    print("MULTI_VIEWS_DONE")


if __name__ == "__main__":
    main()
