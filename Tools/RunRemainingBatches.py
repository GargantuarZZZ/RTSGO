#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""顺序跑完剩余阵营/建筑的 Hunyuan octree 64 批量重建，中间不关服务。"""
import subprocess
import sys

BASES = [
    r"D:\DEV\AI3D-Pipeline\outputs\union_refs_gemini",
    r"D:\DEV\AI3D-Pipeline\outputs\terran_refs_gemini",
    r"D:\DEV\AI3D-Pipeline\outputs\nano_refs_gemini",
    r"D:\DEV\AI3D-Pipeline\outputs\demon_buildings_refs_gemini",
]
PY = r"D:\DEV\Hunyuan3D2\code\venv\Scripts\python.exe"
RUNNER = r"D:\DEV\RTSarcade\Tools\RunHunyuanFromViews.py"


def main():
    for i, base in enumerate(BASES):
        cmd = [
            PY, "-u", "-X", "utf8", RUNNER, base,
            "--textured", "--octree", "64", "--steps", "5",
            "--guidance", "5.0", "--chunks", "8000",
        ]
        if i < len(BASES) - 1:
            cmd.append("--no-shutdown")
        print(f"===== BATCH {i + 1}/{len(BASES)}: {base} =====", flush=True)
        r = subprocess.run(cmd)
        if r.returncode != 0:
            print(f"batch failed: {base}", flush=True)
            sys.exit(r.returncode)
    print("ALL_BATCHES_DONE")


if __name__ == "__main__":
    main()
