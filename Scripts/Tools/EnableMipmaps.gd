extends SceneTree

# =========================================================
# 给 ArtRes/imgs3d 下的 3D 贴图打开 mipmap，并触发重新导入。
#
# 为什么需要：这批贴图是 64~128px 的平铺纹理，却把 `mipmaps/generate` 设成了 false。
# 相机高度可在 600~6000 之间缩放，缩远时一个屏幕像素要覆盖很多 texel，
# 没有 mip 链就只能硬采样 → 地形/菌毯出现明显闪烁与摩尔纹。
# （GLB 模型自带的图集是开了 mipmap 的，只有这批占位贴图没有，属于当年的疏漏。）
#
# 为什么用 Godot 的导入 API 而不是手写覆盖 .import：
# 让引擎决定 params 的完整形状与 uid 处理，避免漏字段导致资源失效。
#
# 运行（需要编辑器模式才有 EditorInterface / reimport_files）：
#   godot --headless --editor --path . --script res://Scripts/Tools/EnableMipmaps.gd
# =========================================================

const TARGET_DIR := "res://ArtRes/imgs3d"

func _initialize() -> void:
	var dir := DirAccess.open(TARGET_DIR)
	if dir == null:
		push_error("[Mipmap] 无法打开目录: " + TARGET_DIR)
		quit(1)
		return

	var to_reimport: PackedStringArray = []
	var changed := 0
	var already := 0
	var failed := 0

	dir.list_dir_begin()
	var file_name := dir.get_next()
	while file_name != "":
		if not dir.current_is_dir() and file_name.ends_with(".png"):
			var res_path := TARGET_DIR + "/" + file_name
			var cfg_path := ProjectSettings.globalize_path(res_path + ".import")

			# .import 是 INI，不是 Resource —— 必须用 ConfigFile 读写
			var imp := ConfigFile.new()
			if imp.load(cfg_path) != OK:
				push_warning("[Mipmap] 读不到 import 文件: " + res_path)
				failed += 1
				file_name = dir.get_next()
				continue

			if imp.get_value("params", "mipmaps/generate", false) == true:
				already += 1
			else:
				imp.set_value("params", "mipmaps/generate", true)
				# detect_3d/compress_to=1 会在检测到 3D 用途时改走 VRAM 压缩并重算 mip；
				# 这里显式置 0，让参数就是我们写下的这一套，行为可预期。
				imp.set_value("params", "detect_3d/compress_to", 0)
				if imp.save(cfg_path) == OK:
					changed += 1
					to_reimport.append(res_path)
					print("[Mipmap] 已开启: ", file_name)
				else:
					push_warning("[Mipmap] 保存失败: " + res_path)
					failed += 1
		file_name = dir.get_next()
	dir.list_dir_end()

	print("[Mipmap] 参数改动=%d 本来就是 true=%d 失败=%d" % [changed, already, failed])

	# 只重导改动过的那批，避免全工程重扫
	if to_reimport.size() > 0:
		if Engine.is_editor_hint() and EditorInterface.get_resource_filesystem() != null:
			EditorInterface.get_resource_filesystem().reimport_files(to_reimport)
			print("[Mipmap] 已触发重新导入 %d 个贴图" % to_reimport.size())
		else:
			push_warning("[Mipmap] 非编辑器模式，无法自动重导；参数已写入，" +
				"下次用编辑器打开工程时会重导。")
	quit(0)
