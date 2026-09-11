#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用 Gemini 图像模型批量生成 2D 正视图参考图（不跑 3D 重建）。

用法:
    python GenReferenceViewsGemini.py prompts.txt out_dir [base_seed] [max_count] [only_index]

环境变量:
    GOOGLE_API_KEY         必填，Google AI Studio 的 API Key
    GEMINI_IMAGE_MODEL     可选，默认 gemini-2.5-flash-image，失败时依次尝试
                           gemini-2.5-flash-image-preview
"""
import base64
import io
import os
import re
import sys

import requests
from PIL import Image

API_BASE = "https://generativelanguage.googleapis.com/v1beta/models"
STYLE = (
    "World of Warcraft style 3D game model, orthographic front view, "
    "perfectly symmetric, front view, facing the camera, centered, entire object visible, "
    "large flat color blocks, flat colors, no shading, no lighting, no gradients, "
    "no detailed textures, no shadows, no rim light, clean solid neutral background, "
    "no text, no watermark, no effects"
)


def slugify(text, limit=48):
    s = re.sub(r"[^\w\u4e00-\u9fff]+", "_", text.strip()).strip("_")
    return s[:limit] or "model"


def call_gemini(prompt, key, model, seed, size=1024):
    url = f"{API_BASE}/{model}:generateContent"
    payload = {
        "contents": [{"parts": [{"text": prompt}]}],
        "generationConfig": {
            "responseModalities": ["TEXT", "IMAGE"],
            "temperature": 1.0,
        },
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
        raise RuntimeError(f"Gemini 没有返回候选图: {str(data)[:300]}")
    for part in candidates[0].get("content", {}).get("parts", []):
        inline = part.get("inlineData")
        if inline and inline.get("data"):
            raw = base64.b64decode(inline["data"])
            return Image.open(io.BytesIO(raw)).convert("RGB")
    raise RuntimeError(f"Gemini 响应里没有图片: {str(candidates[0])[:300]}")


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    prompts_file = sys.argv[1]
    out_root = sys.argv[2]
    base_seed = int(sys.argv[3]) if len(sys.argv) > 3 else 777
    max_count = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    only_index = int(sys.argv[5]) if len(sys.argv) > 5 else 0

    key = os.environ.get("GOOGLE_API_KEY", "")
    if not key:
        print("GOOGLE_API_KEY 未设置")
        sys.exit(1)

    with open(prompts_file, "r", encoding="utf-8") as f:
        prompts = [ln.strip() for ln in f if ln.strip() and not ln.startswith("#")]
    if max_count > 0:
        prompts = prompts[:max_count]
    if only_index > 0:
        prompts = prompts[only_index - 1:only_index]

    models = [os.environ.get("GEMINI_IMAGE_MODEL", "gemini-2.5-flash-image")]
    for fallback in ("gemini-2.5-flash-image-preview",):
        if fallback not in models:
            models.append(fallback)

    os.makedirs(out_root, exist_ok=True)
    last_err = None
    for i, p in enumerate(prompts):
        seed = base_seed + i
        full = f"{p.rstrip('.,，。')}, {STYLE}"
        ok = False
        for model in models:
            try:
                print(f"[{i + 1}/{len(prompts)}] seed={seed} model={model}")
                img = call_gemini(full, key, model, seed)
                d = os.path.join(out_root, f"{i + 1:03d}_{slugify(p)}")
                os.makedirs(d, exist_ok=True)
                img.save(os.path.join(d, "front.png"))
                print(f"saved {d}/front.png")
                ok = True
                break
            except Exception as e:  # noqa: BLE001
                last_err = e
                print(f"  model {model} 失败: {type(e).__name__}: {str(e)[:160]}")
        if not ok:
            print(f"  !! {p} 所有模型都失败")

    if last_err is not None:
        print(f"LAST_ERROR: {type(last_err).__name__}: {str(last_err)[:200]}")
    print("REF_VIEWS_DONE")


if __name__ == "__main__":
    main()
