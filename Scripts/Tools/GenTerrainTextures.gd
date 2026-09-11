extends SceneTree

# =========================================================
# 程序化 PBR 贴图生成器（地面 / 墙体 / 菌毯）
#
# 背景：原有贴图基本是纯色块，实测独立颜色数只有 3~14 个
#   Grass 64x64 14 色 / Wall 64x64 4 色 / NanoCreep 64x64 3 色
# 在相机 600~6000 的缩放范围下，放大就是一片糊，缩远就是闪烁的色斑。
#
# 为什么程序化生成而不是去找免费素材：
#   1. 可复现、可调参：想改草的密度/墙的板缝宽度，改常量重跑即可；
#   2. 自带 normal / roughness，能做到真正的 PBR（免费素材常只给 albedo）；
#   3. 体积极小，且没有第三方授权问题；
#   4. 离线可跑，不依赖任何外部服务。
#
# 关键实现点 —— **可无缝平铺**：
#   全部噪声都用"周期性 value noise"：晶格坐标对 period 取模。
#   直接 rand() 或非周期噪声会让四边对不上，铺开后出现网格状接缝
#   （这在 RTS 俯视大平面上极其显眼），法线也用环绕采样。
#
# 输出 ArtRes/imgs3d/terrain/<Name>_{Albedo,Normal,Roughness}.png
# 运行：godot --headless --editor --path . --script res://Scripts/Tools/GenTerrainTextures.gd
# =========================================================

const OUT_DIR := "res://ArtRes/imgs3d/terrain"
const SIZE := 512          # 默认贴图边长（2 的幂，便于 mipmap）
const OCTAVES := 5

# 每种贴图可以单独指定分辨率。
#
# 为什么墙面需要更高分辨率：贴图的**世界覆盖范围**是由材质里的
# `Uv1Scale`（一张图覆盖多少世界单位）决定的，所以
#     texel/世界单位 = 分辨率 / 覆盖范围
# 地面与墙面都覆盖 512 世界单位：
#     512px 贴图 → 1 texel/单位 → 放大后每个 texel 变成几十像素的**糊块**
#     2048px     → 4 texel/单位 → 细节才立得住
# 地面可以容忍低一些（俯视时草是背景），墙面占屏幕面积大、必须够清。
const SIZE_OVERRIDE := {
	"Wall": 2048,
}

# 每个贴图的调色与参数
#
# ---- 参数设计原则（这是第二版，第一版生成出来"太碎"，在俯视下读成噪点）----
#   · 特征要**大**：RTS 俯视时一格只有几十像素，高频细节会退化成闪烁的沙点。
#     所以 detail_scale 全部调低、detail_strength 也压低。
#   · 对比要**柔**：地面是背景，不能比单位还抢眼。accent 与 base 的差距收窄。
#   · 墙的板缝**每格只留 2×2**：原本 4×4 会让每个 64 单位方块重复一次密集网格。
const RECIPES := {
	"Grass": {
		# 调色**直接对齐原版 Grass.png 的实测值**（不再凭感觉定）：
		#   原版平均 (0.199, 0.718, 0.277)，亮度 123~192（差 70）
		#   albedo 之后还要乘光照/环境光，所以贴图本身要贴住这个亮度，不能自己先压暗。
		# 之前两版都偏暗（第一版 0.355、第二版 0.580），屏幕上就成了发闷的深绿。
		"base": Color(0.196, 0.624, 0.267),      # 深草绿
		"accent": Color(0.259, 0.784, 0.318),    # 亮草绿（≈ 原版主色）
		"dry": Color(0.298, 0.694, 0.290),       # 极轻的黄绿偏移（不做枯草）
		"detail_scale": 5.0,                      # 特征大：俯视下一格才几十像素
		"detail_strength": 0.22,                  # 压低高频，避免"发霉斑块"
		"rough_lo": 0.80,
		"rough_hi": 0.95,
		"bump_strength": 0.22,
		"panel": false,
		"veins": false,
	},
	"Wall": {
		# 关键修正：**不要在图内画板缝网格**。
		#
		# 墙面是"逐块 64³ 立方体"拼出来的（MultiMesh，一格一块）。
		# 如果在贴图内部画 2×2 板缝，那每个方块都会重复一遍这个网格，
		# 相邻方块之间就出现"双重网格、对不齐"的观感 —— 实测确实被评价为垃圾纹理。
		#
		# 正确做法：贴图做成**连续、无内部结构**的金属表面，
		# 让相邻方块自然拼成一体；分块感由立方体本身的受光差异提供就足够了。
		"base": Color(0.412, 0.427, 0.447),      # 冷灰金属（提亮：原来偏暗）
		"accent": Color(0.478, 0.494, 0.514),
		"dry": Color(0.325, 0.333, 0.345),       # 轻微脏污/凹陷
		"detail_scale": 4.0,                      # 大尺度缓慢变化 → 无重复感
		"detail_strength": 0.08,
		"rough_lo": 0.42,
		"rough_hi": 0.60,
		"bump_strength": 0.10,                    # 几乎不给法线，避免方块边缘出亮边
		"panel": false,                           # ★ 关掉图内板缝
		"veins": false,
	},
	"NanoCreep": {
		"base": Color(0.369, 0.196, 0.427),      # 纳米紫（压暗一点，不与单位抢）
		"accent": Color(0.475, 0.267, 0.545),
		"dry": Color(0.243, 0.129, 0.290),
		"detail_scale": 6.0,
		"detail_strength": 0.30,
		"rough_lo": 0.45,
		"rough_hi": 0.68,
		"bump_strength": 0.30,
		"panel": false,
		"veins": true,
	},
	"PlantCreep": {
		"base": Color(0.243, 0.333, 0.196),      # 苔藓绿
		"accent": Color(0.318, 0.412, 0.259),
		"dry": Color(0.192, 0.259, 0.161),
		"detail_scale": 6.0,
		"detail_strength": 0.28,
		"rough_lo": 0.66,
		"rough_hi": 0.88,
		"bump_strength": 0.30,
		"panel": false,
		"veins": true,
	},
}

func _initialize() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT_DIR))

	for name_v in RECIPES.keys():
		var tex_name: String = str(name_v)
		var r: Dictionary = RECIPES[tex_name]
		var size: int = int(SIZE_OVERRIDE.get(tex_name, SIZE))
		print("[GenTex] 生成 %s (%dx%d)…" % [tex_name, size, size])

		var height: PackedFloat32Array = _build_height(r, size)
		var albedo: Image = _build_albedo(r, height, size)
		var rough: Image = _build_roughness(r, height, size)
		var normal: Image = _build_normal(height, float(r["bump_strength"]), size)

		_save(albedo, "%s/%s_Albedo.png" % [OUT_DIR, tex_name])
		_save(normal, "%s/%s_Normal.png" % [OUT_DIR, tex_name])
		_save(rough,  "%s/%s_Roughness.png" % [OUT_DIR, tex_name])

	EditorInterface.get_resource_filesystem().scan()
	print("[GenTex] 完成。输出目录: ", OUT_DIR)
	quit(0)

# ---------------------------------------------------------
# 周期性 value noise：晶格坐标对 period 取模 => 首尾衔接，铺开无接缝
# ---------------------------------------------------------
func _hash2(ix: int, iy: int, seed: int) -> float:
	var h: int = ix * 374761393 + iy * 668265263 + seed * 2246822519
	h = (h ^ (h >> 13)) * 1274126177
	h = h ^ (h >> 16)
	return float(h & 0xFFFFFF) / float(0xFFFFFF)

func _smooth(t: float) -> float:
	return t * t * (3.0 - 2.0 * t)

func _value_noise(x: float, y: float, period: int, seed: int) -> float:
	var ix: int = int(floor(x))
	var iy: int = int(floor(y))
	var fx: float = x - float(ix)
	var fy: float = y - float(iy)

	var x0: int = ((ix % period) + period) % period
	var y0: int = ((iy % period) + period) % period
	var x1: int = (x0 + 1) % period
	var y1: int = (y0 + 1) % period

	var v00: float = _hash2(x0, y0, seed)
	var v10: float = _hash2(x1, y0, seed)
	var v01: float = _hash2(x0, y1, seed)
	var v11: float = _hash2(x1, y1, seed)

	var sx: float = _smooth(fx)
	var sy: float = _smooth(fy)
	return lerp(lerp(v00, v10, sx), lerp(v01, v11, sx), sy)

func _fbm(u: float, v: float, base_freq: int, octaves: int, seed: int) -> float:
	var sum: float = 0.0
	var amp: float = 1.0
	var norm: float = 0.0
	var freq: int = base_freq
	for o in octaves:
		sum += _value_noise(u * float(freq), v * float(freq), freq, seed + o * 131) * amp
		norm += amp
		amp *= 0.5
		freq *= 2
	return sum / max(norm, 0.0001)

# ---------------------------------------------------------
# 高度场（推法线用，同时让 albedo 有明暗层次）
# ---------------------------------------------------------
func _build_height(r: Dictionary, size: int) -> PackedFloat32Array:
	var out := PackedFloat32Array()
	out.resize(size * size)

	var detail_scale: float = float(r["detail_scale"])
	var detail_strength: float = float(r["detail_strength"])
	var panel: bool = bool(r["panel"])
	var veins: bool = bool(r["veins"])

	# 基础频率固定为 4（2 的幂，与贴图边长对齐不会出现周期错位）
	var base_freq: int = 4
	var detail_freq: int = base_freq * int(detail_scale / 4.0) + 4

	for y in size:
		for x in size:
			var u: float = float(x) / float(size)
			var v: float = float(y) / float(size)

			var h: float = _fbm(u, v, base_freq, OCTAVES, 11)
			h = lerp(h, _fbm(u, v, detail_freq, 3, 77), detail_strength)
			# 对比拉伸：fBm 天然集中在 0.5 附近，不拉的话 albedo 会糊成一片中间色
			# （第一版草地"又暗又闷、斑块发霉"就是这个原因）。
			h = clampf((h - 0.5) * 1.5 + 0.5, 0.0, 1.0);

			if panel:
				h = _apply_panel(h, u, v, float(r.get("panel_cells", 2.0)))
			if veins:
				h = _apply_veins(h, u, v)

			out[y * size + x] = clampf(h, 0.0, 1.0)
	return out

# 装甲板缝：按 cells×cells 分格，格线处做**柔和倒角**
#
# 关键教训：第一版板缝两侧是"悬崖"，法线图被推成极值，
# 渲染出来是刺眼的品红/青色镶边。板缝必须是**缓坡**：
#   · 缝宽 0.035 → 0.05（稍微宽一点，坡度更缓）
#   · 倒角过渡 0.10 → 0.22（拉长过渡区，坡度大幅下降）
#   · 高度落差 0.22 → 0.14（减小落差）
func _apply_panel(h: float, u: float, v: float, cells: float) -> float:
	var fu: float = fposmod(u * cells, 1.0)
	var fv: float = fposmod(v * cells, 1.0)
	var edge: float = min(min(fu, 1.0 - fu), min(fv, 1.0 - fv))
	var line: float = smoothstep(0.0, 0.05, edge)    # 0 在缝里，1 在板面
	var bevel: float = smoothstep(0.0, 0.22, edge)
	return h * 0.72 + line * 0.16 + bevel * 0.12

# 菌毯脉络：脊状噪声做出"血管状"凸起
func _apply_veins(h: float, u: float, v: float) -> float:
	var n: float = _fbm(u, v, 6, 4, 313)
	var ridge: float = 1.0 - abs(n - 0.5) * 2.0
	ridge = pow(ridge, 3.0)
	return h * 0.7 + ridge * 0.3

# ---------------------------------------------------------
# Albedo：base→accent 按高度渐变，低处压暗（缝隙/沟壑）
# ---------------------------------------------------------
func _build_albedo(r: Dictionary, height: PackedFloat32Array, size: int) -> Image:
	var img: Image = Image.create(size, size, false, Image.FORMAT_RGB8)
	var base: Color = r["base"]
	var accent: Color = r["accent"]
	var dark: Color = r["dry"]

	for y in size:
		for x in size:
			var h: float = height[y * size + x]
			var c: Color = base.lerp(accent, clampf((h - 0.35) / 0.45, 0.0, 1.0))
			# 低处（沟壑）压暗，但只混 40% 且用偏绿的 dark，避免把草地压成灰
			c = c.lerp(dark, clampf((0.42 - h) / 0.42, 0.0, 1.0) * 0.40)
			c = c.lightened(clampf((h - 0.72) / 0.28, 0.0, 1.0) * 0.18)
			img.set_pixel(x, y, c)
	return img

# ---------------------------------------------------------
# Roughness：高度映射到给定区间（沟壑更粗糙、凸面更光滑）
# ---------------------------------------------------------
func _build_roughness(r: Dictionary, height: PackedFloat32Array, size: int) -> Image:
	var img: Image = Image.create(size, size, false, Image.FORMAT_RGB8)
	var lo: float = float(r["rough_lo"])
	var hi: float = float(r["rough_hi"])
	for y in size:
		for x in size:
			var h: float = height[y * size + x]
			var rough: float = lerp(lo, hi, 1.0 - h)
			img.set_pixel(x, y, Color(rough, rough, rough))
	return img

# ---------------------------------------------------------
# 法线：对高度场做 Sobel，编码到 RGB；采样环绕以保证边缘连续
# ---------------------------------------------------------
func _build_normal(height: PackedFloat32Array, strength: float, size: int) -> Image:
	var img: Image = Image.create(size, size, false, Image.FORMAT_RGB8)
	for y in size:
		for x in size:
			var xl: int = (x - 1 + size) % size
			var xr: int = (x + 1) % size
			var yu: int = (y - 1 + size) % size
			var yd: int = (y + 1) % size

			var dx: float = (height[y * size + xr] - height[y * size + xl]) * strength * 12.0
			var dy: float = (height[yd * size + x] - height[yu * size + x]) * strength * 12.0

			var n: Vector3 = Vector3(-dx, -dy, 1.0).normalized()
			img.set_pixel(x, y, Color(n.x * 0.5 + 0.5, n.y * 0.5 + 0.5, n.z * 0.5 + 0.5))
	return img

func _save(img: Image, res_path: String) -> void:
	var err: int = img.save_png(ProjectSettings.globalize_path(res_path))
	if err != OK:
		push_error("[GenTex] 保存失败: %s (err=%d)" % [res_path, err])
	else:
		print("[GenTex]   -> ", res_path)
