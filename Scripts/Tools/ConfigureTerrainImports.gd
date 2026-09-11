extends SceneTree

# =========================================================
# 为程序化生成的地形贴图设置正确的导入参数。
#
# 为什么必须单独做这一步：
#   默认导入按"通用贴图"处理 —— albedo 是对的，但
#   · Normal 贴图会被当成 sRGB 颜色，法线方向被 gamma 曲线掰弯，光照会怪；
#   · Roughness 只是灰度数据，同样不该走 sRGB；
#   而且新生成的 PNG 默认 `mipmaps/generate=false`，
#   512 贴图铺满大平面缩远时会剧烈闪烁（旧 64px 贴图就是这个毛病）。
#
# 规则：
#   *_Albedo    → 保留 sRGB、开 mipmap
#   *_Normal    → 关闭 sRGB、勾 normal_map、开 mipmap
#   *_Roughness → 关闭 sRGB、开 mipmap
#
# 幂等：重复跑只会把参数再写一遍。
# 运行：godot --headless --editor --path . --script res://Scripts/Tools/ConfigureTerrainImports.gd
# =========================================================

const TARGET_DIR := "res://ArtRes/imgs3d/terrain"

func _initialize() -> void:
	var dir := DirAccess.open(TARGET_DIR)
	if dir == null:
		push_error("[TexImport] 无法打开目录: " + TARGET_DIR)
		quit(1)
		return

	var changed := 0
	var failed := 0
	var to_reimport := PackedStringArray()

	dir.list_dir_begin()
	var file_name := dir.get_next()
	while file_name != "":
		if not dir.current_is_dir() and file_name.ends_with(".png"):
			var res_path := TARGET_DIR + "/" + file_name
			var cfg_path := ProjectSettings.globalize_path(res_path + ".import")

			var imp := ConfigFile.new()
			if imp.load(cfg_path) != OK:
				push_warning("[TexImport] 读不到: " + cfg_path)
				failed += 1
				file_name = dir.get_next()
				continue

			var is_normal: bool = file_name.ends_with("_Normal.png")
			var is_data: bool = file_name.ends_with("_Roughness.png")

			# 所有地形贴图都要 mipmap（缩远采样）
			imp.set_value("params", "mipmaps/generate", true)
			# 关闭"检测到 3D 就转 VRAM 压缩"：让参数就是我们写下的这一套
			imp.set_value("params", "detect_3d/compress_to", 0)
			# 不重复压缩、保持无损，避免程序化细节被压掉
			imp.set_value("params", "compress/mode", 0)

			if is_normal:
				# 关键：法线图是**数据**不是颜色，必须线性 + 标记为法线图
				imp.set_value("params", "compress/normal_map", 1)
				imp.set_value("params", "process/hdr_as_srgb", false)
			elif is_data:
				imp.set_value("params", "compress/normal_map", 0)
				imp.set_value("params", "process/hdr_as_srgb", false)
			else:
				# albedo 保持默认（sRGB）
				imp.set_value("params", "compress/normal_map", 0)

			if imp.save(cfg_path) == OK:
				changed += 1
				to_reimport.append(res_path)
			else:
				push_warning("[TexImport] 保存失败: " + cfg_path)
				failed += 1
		file_name = dir.get_next()
	dir.list_dir_end()

	print("[TexImport] 配置 %d 个贴图，失败 %d" % [changed, failed])

	if to_reimport.size() > 0 and Engine.is_editor_hint():
		var fs := EditorInterface.get_resource_filesystem()
		if fs != null:
			fs.scan()
			print("[TexImport] 已触发扫描重导")
	quit(0)
