#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""批量更新 UnitModelLibrary.cs 中指定实体的模型块（保留特殊字段）。"""
import io
import os

PATH = r"D:\DEV\RTSarcade\Scripts\Core\UnitModelLibrary.cs"

# key -> 字段行（不含缩进；结尾逗号由脚本统一处理）
SPECS = {
    "SCV": [
        'ModelPath = "res://ArtRes/models/Union/SCV.glb"',
        "Scale = 26.45f, OffsetY = 24.81f",
        "YMin = -0.94f, YMax = 0.89f",
        "TurnWhileMoving = false",
    ],
    "RifleMan": [
        'ModelPath = "res://ArtRes/models/Union/RifleMan.glb"',
        "Scale = 6.71f, OffsetY = 6.69f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun"',
    ],
    "RocketMan": [
        'ModelPath = "res://ArtRes/models/Union/RocketMan.glb"',
        "Scale = 6.71f, OffsetY = 6.69f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun"',
    ],
    "Medic": [
        'ModelPath = "res://ArtRes/models/Union/Medic.glb"',
        "Scale = 6.71f, OffsetY = 6.69f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun"',
    ],
    "Biped": [
        'ModelPath = "res://ArtRes/models/Union/Biped.glb"',
        "Scale = 46.75f, OffsetY = 46.58f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle", MoveAnimation = "Walk"',
    ],
    "Hp": [
        'ModelPath = "res://ArtRes/models/Union/Hp.glb"',
        "Scale = 49.58f, OffsetY = 21.57f",
        "YMin = -0.44f, YMax = 0.4f",
    ],
    "BattleCruiser": [
        'ModelPath = "res://ArtRes/models/Union/BattleCruiser.glb"',
        "Scale = 60.77f, OffsetY = 15.98f",
        "YMin = -0.26f, YMax = 0.22f",
    ],
    "CommandCenter": [
        'ModelPath = "res://ArtRes/models/Union/CommandCenter.glb"',
        "Scale = 94.97f, OffsetY = 73.8f",
        "YMin = -0.78f, YMax = 0.71f",
    ],
    "BB": [
        'ModelPath = "res://ArtRes/models/Union/BB.glb"',
        "Scale = 66.34f, OffsetY = 66.09f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "VF": [
        'ModelPath = "res://ArtRes/models/Union/VF.glb"',
        "Scale = 147.1f, OffsetY = 100.6f",
        "YMin = -0.68f, YMax = 0.62f",
    ],
    "SupplyDepot": [
        'ModelPath = "res://ArtRes/models/Union/SupplyDepot.glb"',
        "Scale = 38.68f, OffsetY = 38.56f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "Academy": [
        'ModelPath = "res://ArtRes/models/Union/Academy.glb"',
        "Scale = 67.99f, OffsetY = 67.71f",
        "YMin = -1f, YMax = 0.89f",
    ],
    "Starport": [
        'ModelPath = "res://ArtRes/models/Union/Starport.glb"',
        "Scale = 146.46f, OffsetY = 100.55f",
        "YMin = -0.69f, YMax = 0.62f",
    ],
    "OrbitalControl": [
        'ModelPath = "res://ArtRes/models/Union/OrbitalControl.glb"',
        "Scale = 113.82f, OffsetY = 112.17f",
        "YMin = -0.99f, YMax = 0.92f",
    ],
    "Engineer": [
        'ModelPath = "res://ArtRes/models/Terran/Engineer.glb"',
        "Scale = 25.03f, OffsetY = 24.94f",
        "YMin = -1f, YMax = 0.93f",
        "TurnWhileMoving = false",
    ],
    "Marine": [
        'ModelPath = "res://ArtRes/models/Terran/Marine.glb"',
        "Scale = 6.71f, OffsetY = 6.69f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun"',
    ],
    "HeavyInfantry": [
        'ModelPath = "res://ArtRes/models/Terran/HeavyInfantry.glb"',
        "Scale = 8.39f, OffsetY = 8.36f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle_Gun", MoveAnimation = "Run_Gun"',
    ],
    "CommandVehicle": [
        'ModelPath = "res://ArtRes/models/Terran/CommandVehicle.glb"',
        "Scale = 53.35f, OffsetY = 46.54f",
        "YMin = -0.87f, YMax = 0.55f",
    ],
    "Fighter": [
        'ModelPath = "res://ArtRes/models/Terran/Fighter.glb"',
        "Scale = 50.86f, OffsetY = 17.15f",
        "YMin = -0.34f, YMax = 0.28f",
    ],
    "MissileVehicle": [
        'ModelPath = "res://ArtRes/models/Terran/MissileVehicle.glb"',
        "Scale = 56.79f, OffsetY = 47.67f",
        "YMin = -0.84f, YMax = 0.78f",
    ],
    "FortressCore": [
        'ModelPath = "res://ArtRes/models/Terran/FortressCore.glb"',
        "Scale = 114.74f, OffsetY = 74.9f",
        "YMin = -0.65f, YMax = 0.58f",
    ],
    "FrontlineCamp": [
        'ModelPath = "res://ArtRes/models/Terran/FrontlineCamp.glb"',
        "Scale = 66.34f, OffsetY = 66.15f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "FieldCamp": [
        'ModelPath = "res://ArtRes/models/Terran/FieldCamp.glb"',
        "Scale = 66.4f, OffsetY = 66.06f",
        "YMin = -0.99f, YMax = 0.93f",
    ],
    "ArmorFactory": [
        'ModelPath = "res://ArtRes/models/Terran/ArmorFactory.glb"',
        "Scale = 185.56f, OffsetY = 101.39f",
        "YMin = -0.55f, YMax = 0.49f",
    ],
    "Airfield": [
        'ModelPath = "res://ArtRes/models/Terran/Airfield.glb"',
        "Scale = 163.19f, OffsetY = 101.04f",
        "YMin = -0.62f, YMax = 0.56f",
    ],
    "Armory": [
        'ModelPath = "res://ArtRes/models/Terran/Armory.glb"',
        "Scale = 73.84f, OffsetY = 66.32f",
        "YMin = -0.9f, YMax = 0.84f",
    ],
    "AlgaeFactory": [
        'ModelPath = "res://ArtRes/models/Terran/AlgaeFactory.glb"',
        "Scale = 74.61f, OffsetY = 74.33f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "NanoBehemoth": [
        'ModelPath = "res://ArtRes/models/Nano/NanoBehemoth.glb"',
        "Scale = 96.07f, OffsetY = 95.73f",
        "YMin = -1f, YMax = 0.93f",
        'IdleAnimation = "Idle", MoveAnimation = "Walk"',
    ],
    "NanoTurret": [
        'ModelPath = "res://ArtRes/models/Nano/NanoTurret.glb"',
        "Scale = 57.92f, OffsetY = 36.74f",
        "YMin = -0.63f, YMax = 0.57f",
        "RotateToAim = true",
    ],
    "NanoSniper": [
        'ModelPath = "res://ArtRes/models/Nano/NanoSniper.glb"',
        "Scale = 120.12f, OffsetY = 119.63f",
        "YMin = -1f, YMax = 0.93f",
        "RotateToAim = true",
    ],
    "NanoAA": [
        'ModelPath = "res://ArtRes/models/Nano/NanoAA.glb"',
        "Scale = 80.08f, OffsetY = 79.75f",
        "YMin = -1f, YMax = 0.94f",
        "RotateToAim = true",
    ],
    "NanoHarvester": [
        'ModelPath = "res://ArtRes/models/Nano/NanoHarvester.glb"',
        "Scale = 169.83f, OffsetY = 84.49f",
        "YMin = -0.5f, YMax = 0.44f",
    ],
    "NanoActiveTower": [
        'ModelPath = "res://ArtRes/models/Nano/NanoActiveTower.glb"',
        "Scale = 106.92f, OffsetY = 106.5f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "NanoSmokeTower": [
        'ModelPath = "res://ArtRes/models/Nano/NanoSmokeTower.glb"',
        "Scale = 78.57f, OffsetY = 78.3f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "NanoCore": [
        'ModelPath = "res://ArtRes/models/Nano/NanoCore.glb"',
        "Scale = 82.09f, OffsetY = 81.71f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "HellCity": [
        'ModelPath = "res://ArtRes/models/Demon/HellCity.glb"',
        "Scale = 76.63f, OffsetY = 76.38f",
        "YMin = -1f, YMax = 0.92f",
    ],
    "GreatRift": [
        'ModelPath = "res://ArtRes/models/Demon/GreatRift.glb"',
        "Scale = 75.29f, OffsetY = 74.99f",
        "YMin = -1f, YMax = 0.92f",
    ],
    "HeavyWorkshop": [
        'ModelPath = "res://ArtRes/models/Demon/HeavyWorkshop.glb"',
        "Scale = 99.87f, OffsetY = 99.5f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "DragonNest": [
        'ModelPath = "res://ArtRes/models/Demon/DragonNest.glb"',
        "Scale = 88.87f, OffsetY = 82.82f",
        "YMin = -0.93f, YMax = 0.87f",
    ],
    "ManaTower": [
        'ModelPath = "res://ArtRes/models/Demon/ManaTower.glb"',
        "Scale = 68.98f, OffsetY = 68.72f",
        "YMin = -1f, YMax = 0.89f",
        "RotateToAim = true",
    ],
    "SoulStone": [
        'ModelPath = "res://ArtRes/models/Demon/SoulStone.glb"',
        "Scale = 25.9f, OffsetY = 25.8f",
        "YMin = -1f, YMax = 0.93f",
    ],
    "LavaAltar": [
        'ModelPath = "res://ArtRes/models/Demon/LavaAltar.glb"',
        "Scale = 87.04f, OffsetY = 70.37f",
        "YMin = -0.81f, YMax = 0.75f",
    ],
    "CurseFortress": [
        'ModelPath = "res://ArtRes/models/Demon/CurseFortress.glb"',
        "Scale = 89.83f, OffsetY = 89.51f",
        "YMin = -1f, YMax = 0.88f",
    ],
    "DemonTower": [
        'ModelPath = "res://ArtRes/models/Demon/DemonTower.glb"',
        "Scale = 87.83f, OffsetY = 87.48f",
        "YMin = -1f, YMax = 0.83f",
    ],
}


def replace_block(text, key, fields):
    marker = f'["{key}"] = new UnitModelInfo'
    start = text.find(marker)
    if start < 0:
        raise RuntimeError(f"key not found: {key}")
    brace = text.find("{", start)
    depth = 0
    i = brace
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    body = "\n".join("\t\t\t\t" + ln + "," for ln in fields)
    new_block = "{\n" + body + "\n\t\t\t}"
    return text[:brace] + new_block + text[i + 1:]


def main():
    with io.open(PATH, "r", encoding="utf-8") as f:
        text = f.read()
    for key, fields in SPECS.items():
        text = replace_block(text, key, fields)
    with io.open(PATH, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print(f"updated {len(SPECS)} entries")


if __name__ == "__main__":
    main()
