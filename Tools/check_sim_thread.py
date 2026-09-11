#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""P0-1 M4：静态扫描模拟线程路径上的 Godot API 调用。

扫目录：Scripts/Units/Actions, Scripts/Units/Modules(排除纯视觉/标签/FX),
Scripts/Units/Weapons, Scripts/Core/SimManager.cs, Scripts/Simulation。
命中即报错（视觉镜像层、EnqueueMain/CallDeferred 包装、注释/字符串除外）。
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "Scripts"))
SCAN_DIRS = [
    os.path.join(ROOT, "Units", "Actions"),
    os.path.join(ROOT, "Units", "Modules"),
    os.path.join(ROOT, "Units", "Weapons"),
]
SCAN_FILES = [
    os.path.join(ROOT, "Core", "SimManager.cs"),
    os.path.join(ROOT, "Simulation"),
]
SCAN_FILES.extend(os.path.join(ROOT, "Core", name)
                  for name in sorted(os.listdir(os.path.join(ROOT, "Core")))
                  if name.startswith("SimManager.BotAI.") and name.endswith(".cs"))

PATTERNS = [
    r"\bGetNode\w*\s*\(",
    r"\bCreateTween\s*\(",
    r"\.QueueFree\s*\(",
    r"\.AddChild\s*\(",
    r"\.RemoveChild\s*\(",
    r"\.Instantiate\s*\(",
    r"\bGetViewport\s*\(",
]

EXCLUDE_FILES = {
    "UnitVisuals.cs",
    "AuraRangeVisual.cs",
    "HarvesterMiningFX.cs",
    "BoundCountLabel3D.cs",
    "GarrisonCountLabel3D.cs",
    "ResourceAmountLabel.cs",
    "HomingProjectile.cs",
    "SimPathfinder.cs",
}

SAFE_SUBSTRINGS = (
    "EnqueueMain",
    "CallDeferred",
    "SetDeferred",
    "MainThread",
    "//",
    "GetFirePointWorld",
    "DeferredAim",
)


def strip_comment(line):
    out = []
    in_str = False
    i = 0
    while i < len(line):
        ch = line[i]
        if ch == '"':
            in_str = not in_str
        elif ch == "/" and i + 1 < len(line) and line[i + 1] == "/" and not in_str:
            break
        out.append(ch)
        i += 1
    return "".join(out)


def main():
    hits = []
    files = []
    for d in SCAN_DIRS:
        for root, _, names in os.walk(d):
            for n in names:
                if n.endswith(".cs") and n not in EXCLUDE_FILES:
                    files.append(os.path.join(root, n))
    for p in SCAN_FILES:
        if os.path.isdir(p):
            for root, _, names in os.walk(p):
                for n in names:
                    if n.endswith(".cs") and n not in EXCLUDE_FILES:
                        files.append(os.path.join(root, n))
        elif os.path.isfile(p):
            files.append(p)

    for path in sorted(set(files)):
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            in_main_method = False
            main_brace_depth = 0
            in_enqueue_lambda = 0
            for lineno, raw in enumerate(f, 1):
                line = strip_comment(raw)
                if not line.strip():
                    continue
                # Independent simulation must not depend on host services or Godot.
                if os.path.commonpath([path, os.path.join(ROOT, "Simulation")]) == os.path.join(ROOT, "Simulation"):
                    if re.search(r"\busing\s+Godot\b|\bGodot\.|\bRTS\.(Core|World)\.", line):
                        hits.append(f"{os.path.relpath(path, ROOT)}:{lineno}: simulation host dependency: {line.strip()}")
                if re.search(r"\b\w*Main\s*\([^)]*\)\s*\{", line):
                    in_main_method = True
                    main_brace_depth = 1
                    continue
                if in_main_method:
                    main_brace_depth += line.count("{") - line.count("}")
                    if main_brace_depth <= 0:
                        in_main_method = False
                    continue
                if "EnqueueMain(" in raw:
                    in_enqueue_lambda += 1
                    continue
                if in_enqueue_lambda > 0:
                    if raw.lstrip().startswith("});") or raw.lstrip().startswith("});"):
                        in_enqueue_lambda -= 1
                    continue
                if any(s in raw for s in SAFE_SUBSTRINGS):
                    continue
                for pat in PATTERNS:
                    if re.search(pat, line):
                        hits.append(f"{path.replace(ROOT + os.sep, '')}:{lineno}: {raw.strip()}")
                        break

    if hits:
        print("\n".join(hits[:60]))
        print(f"SIM_THREAD_CHECK_FAIL {len(hits)} hits")
        return 1
    print("SIM_THREAD_CHECK_PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
