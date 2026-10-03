#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把设计表里的文本同步进 .tres 配置（只改文本，不动数值）。

设计数据表是权威来源。本脚本处理：
  · 科技配置的 DisplayName / Description  ← 表格「科技」「效果」两列
  · 种族配置的 DisplayName                ← 表格名（工作表名）

注意两张表的「科技」段落列布局**不一样**，不能共用一个列索引：
  巫师    : 0=科技 1=类型 2=需求 5=研发时间 6=资源 7=效果
  AI指挥系统: 0=科技 1=类型 2=需求 3=研发时间 4=资源 5=效果
所以下面按 sheet 分别给列号。

用法:
    python Tools/SyncExcelText.py --check   # 只比对，不改
    python Tools/SyncExcelText.py           # 写回配置
"""
import argparse
import io
import os
import re
import sys

import openpyxl

EXCEL = r"D:\DEV\RTSarcade\设计\设计数据表 (version 1).xlsb.xlsx"
CFG = r"D:\DEV\RTSarcade\Data\Configs"

# (工作表, 首行, 末行, 名称列, 效果列, 游戏 ID 列表)
TECH_BLOCKS = [
    ("巫师", 28, 32, 0, 7,
     ["WizTechPuppetRebirth", "WizTechRocketFist", "WizTechShortTeleport",
      "WizTechMagicInstrument", "WizTechElementBoost"]),
    ("AI指挥系统", 27, 41, 0, 5,
     ["AITechDrill", "AITechEngineering", "AITechAssemblyLine", "AITechMonoform",
      "AITechLightArmor", "AITechPropeller", "AITechFullSalvo", "AITechElectronicWarfare",
      "AITechArsenalPlan", "AITechExpandHangar", "AITechStableProd", "AITechSwarmAero",
      "AITechDamageCalc", "AITechRecycle", "AITechSuperManeuver"]),
]

# 种族显示名：工作表名就是表格里的写法（"AI指挥系统" 中间没有空格）
RACE_NAMES = {"Wizard": "巫师", "AICommand": "AI指挥系统"}


def read_cfg(path):
    return io.open(path, encoding="utf-8").read()


def set_text(src, field, value):
    """把 `field = "..."` 替换成新值（只改这一行）。"""
    pat = re.compile(r'(?m)^' + re.escape(field) + r' = ".*"$')
    if pat.search(src):
        return pat.sub(f'{field} = "{value}"', src, count=1)
    # 字段不存在：插在 script = ExtResource 那一行之后
    marker = re.search(r'(?m)^script = ExtResource\("1"\)$', src)
    if marker:
        at = marker.end()
        return src[:at] + f'\n{field} = "{value}"' + src[at:]
    return src


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只比对不写")
    args = ap.parse_args()

    wb = openpyxl.load_workbook(EXCEL, data_only=True)
    changes = []

    for sheet, first, last, name_col, eff_col, ids in TECH_BLOCKS:
        ws = wb[sheet]
        table = list(ws.iter_rows(min_row=first + 1, max_row=last + 1, values_only=True))
        for i, uid in enumerate(ids):
            if i >= len(table):
                break
            row = table[i]
            name = str(row[name_col] or "").strip()
            eff = str(row[eff_col] or "").strip() if eff_col < len(row) else ""
            path = os.path.join(CFG, uid + ".tres")
            if not os.path.exists(path):
                print(f"MISS {uid}")
                continue
            src = read_cfg(path)
            for field, want in (("DisplayName", name), ("Description", eff)):
                if not want:
                    continue
                cur = re.search(r'(?m)^' + field + r' = "(.*)"$', src)
                have = cur.group(1) if cur else ""
                if have == want:
                    continue
                changes.append((uid, field, have, want, path))

    for race, name in RACE_NAMES.items():
        path = os.path.join(CFG, race + ".tres")
        src = read_cfg(path)
        cur = re.search(r'(?m)^DisplayName = "(.*)"$', src)
        have = cur.group(1) if cur else ""
        if have != name:
            changes.append((race, "DisplayName", have, name, path))

    print(f"需要同步的文本 {len(changes)} 处：")
    for uid, field, have, want, _ in changes:
        print(f"  {uid}.{field}")
        print(f"      旧: {have}")
        print(f"      新: {want}")

    if args.check or not changes:
        return

    by_file = {}
    for uid, field, _, want, path in changes:
        by_file.setdefault(path, []).append((field, want))
    for path, edits in by_file.items():
        src = read_cfg(path)
        for field, want in edits:
            src = set_text(src, field, want)
        io.open(path, "w", encoding="utf-8", newline="\n").write(src)
    print(f"\n已写回 {len(by_file)} 个配置文件")


if __name__ == "__main__":
    main()
