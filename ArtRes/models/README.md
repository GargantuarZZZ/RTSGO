# 3D 模型来源与 AI 生成流程

## 模型来源

- 大多数 `.glb` 模型来自 **Quaternius – Ultimate Space Kit**（CC0）：
  - 官网：https://quaternius.com/packs/ultimatespacekit.html
- 炮塔模型（`turrets/*.obj`）来自 **Quaternius – Steampunk Turret Pack**（CC0）：
  - 官网：https://quaternius.com/packs/turretpack.html
- 新阵营模型（恶魔/多足）由 **Gemini 2.5 Flash Image 出图 + 本地 Hunyuan3D-2mv 重建**生成，见下。

模型映射关系见 `Scripts/Core/UnitModelLibrary.cs`。

---

## AI 生成模型标准流程（恶魔 / 多足等阵营）

> 任何新阵营/新单位必须严格按本流程执行，不要凭感觉改 prompt。

### 0. 数据来源

阵营单位表在 `C:\Users\18131\Desktop\o2\rts\设计数据表 - 副本.xlsx`（对应工作表）。
先完整读取工作表，**只提取外观能表达的信息**：体型（长宽）、装甲类型、武器造型、姿态、数量/规模。

### 1. 写 Prompt（纯外观，禁止机制词）

每条 prompt 只描述**看得见的东西**：

- 身体/腿数/翅膀/尾巴/披风等造型
- 装甲厚薄（轻甲→轻巧，重甲→笨重）
- 武器**造型**（嘴里的牙、背上的炮、爪里的枪），不写攻击方式
- 姿态（站立/奔跑/悬浮/低伏）

**禁止出现**：对地/对空、AOE、弹道、即时、攻速、射程、人口、技能、机制、视野、采集、建造、自爆、移动速度等游戏术语。
**禁止特效**：发光、火焰、魔法、爆炸、能量、粒子、光环、透明光效；prompt 末尾也要显式加 `no effects, no glow, no particles, no flames, no magic, no aura, no explosion, no energy`。
**不写死占比**（不要写 “occupies X% of frame”），生成完在游戏里按包围盒调尺寸。
**每个单位必须写清自己的主色/材质**（如火焰巨魔=暗红+岩浆橙裂纹、重型战车=暗铁色）；配色属于单位 prompt，不属于风格锚点，否则 Gemini 会乱配色（火焰巨魔纯黑、坦克纯白）。
**主体必须大且居中、占满画面**（只留小边距）：画面里主体占比太小会导致 3D 重建精度不足。这是统一要求，写在风格锚点里；不要在各单位 prompt 里写不同的“占比”。

### 2. 出图（只出正面 + 右面）

```bat
python Tools\GenWandererRefs.py Tools\<阵营>Prompts.txt D:\DEV\AI3D-Pipeline\outputs\<阵营>_refs_gemini <seed> --subject creature --anchor Tools\StyleAnchors\<阵营>.txt
```

- 只生成 `front.png` 和 `right.png`，`back/left` 一律留空。
- **右视图定义（重要）**：正面朝图片**右**、背面朝图片**左**才是右视图；“正面朝左”是左视图，不要搞反。
- **必须带 `--anchor`**：每个阵营有自己的**风格锚点文本文件**（`Tools/StyleAnchors/<阵营>.txt`），例如：
  - 恶魔：魔兽世界游戏模型画风、低模、平涂、剪影厚实
  - 多足：科幻低模游戏资产画风、机械单位展示感、干净棱角造型
  - 锚点是**文字**，不是参考图；它只定义**画风/渲染风格**，**禁止写配色、装甲、角、武器等具体外观**——那些只能由表格那行 prompt 决定，否则会把不存在的特征强加给所有单位（例如幽灵也被画成红黑装甲）。
- 出完图先给用户审，通过后再进 3D。

### 3. 3D 重建（面数在生成时控制）

```bat
python Tools\RunHunyuanFromViews.py D:\DEV\AI3D-Pipeline\outputs\<阵营>_refs_gemini --textured --octree 32 --steps 5 --guidance 5.0 --chunks 8000
```

- **一律 `--octree 32`**：生成出来直接 ≤1 万三角面。
- **禁止事后减面**（pymeshlab/trimesh 二次减面会把模型拆出缝）。面数超标就重生成，不要减面。

### 4. 替换进游戏

1. 用 `Tools\GlbInfo\bin\Release\net8.0\GlbInfo.exe <glb>` 量包围盒（YMin/YMax）。
2. 把 `textured_mesh.glb` 复制为 `ArtRes\models\<阵营>\<Unit>.glb`。
3. 更新 `Scripts/Core/UnitModelLibrary.cs`：`Scale/OffsetY/YMin/YMax`。
   - 1 格 = 64 世界单位；`OffsetY = -YMin * Scale`。
   - **模型尺寸 = 实际占地**：`Scale = 占地尺寸 / 模型水平最大跨度`（单位按 `FootprintTiles`，建筑按 `GridWidth/Height`，缺省 1 格）；不要再沿用旧占位模型的视觉高度。
   - `RotationY` 保持原值，**不要主动改朝向**（除非用户明确要求）。
4. Godot 重新导入：
   ```bat
   Godot_v4.5.1-stable_mono_win64_console.exe --headless --path . --import
   ```
5. **必须重新导出预制体**（否则游戏里还是旧模型！）：
   ```bat
   $env:EXPORT_ONLY='Builder,Hunter,...'; Godot_v4.5.1-stable_mono_win64_console.exe --path . res://Scenes/Tools/ExportEntityScenes.tscn
   ```
   无头模式跑导出会崩，必须用窗口模式（会闪一下窗口）。
6. 编译：
   ```bat
   dotnet build RTSarcade.csproj -c Debug
   ```

---

## 踩过的坑（必须避免）

| 错误 | 后果 | 正确做法 |
|---|---|---|
| prompt 里写死占比 | 尺寸与游戏不符 | 不写占比，生成后按盒子调 |
| 右视图写“正面朝图片左” | 实际是左视图，模型左右反 | 正面朝右=右视图 |
| prompt 写游戏机制词（AOE/对地/对空/弹道等） | 图像模型乱画 | 只写外观 |
| prompt/图带特效（发光/火焰/魔法/爆炸） | 画面全是特效 | 显式禁特效 |
| 单位 prompt 不写配色/材质 | Gemini 乱配色（火巨魔纯黑、战车纯白） | 每个单位 prompt 写明主色与材质 |
| 主体在画面里占比太小 | 重建精度差、模型糊 | 锚点统一要求“主体占满画面、小边距” |
| 把风格锚点做成图片/锁死全局配色 | 所有单位长得一样 | 锚点是每个阵营独立的文字文件，只统一阵营风格 |
| 用通用格式串当锚点 | 风格漂移、不统一 | 每个阵营一个 `Tools/StyleAnchors/<阵营>.txt`，必须带 `--anchor` |
| 事后用 pymeshlab 减面 | 模型全是缝 | octree 32 生成时就控面数 |
| 只改 UnitModelLibrary 不重导预制体 | 游戏里还是旧模型 | 重新跑 ExportEntityScenes |
| headless 跑 ExportEntityScenes | 崩溃 | 窗口模式跑 |
| 擅自改 RotationY | 朝向不符合预期 | 保持原值，除非用户要求 |
