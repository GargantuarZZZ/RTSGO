extends SceneTree

# =========================================================
# 死资源审计（先验证，后删除）
#
# 背景：审计指出 ArtRes 里有大量"疑似死资源"，但**删除不可逆**，
# 而审计结论来自另一个 agent。所以这里只做一件事：
# 把每条结论**逐条独立验证**并落成清单，由人确认后再删。
#
# 验证项：
#   A. `ArtRes/models/**/<Name>_0.png`：是否 (1) 内容已在同名 GLB 内 (2) 全项目无引用
#   B. 其它无引用的 .glb/.obj/.mtl/.fbx/.png（带保留白名单）
#
# 运行：godot --headless --editor --path . --script res://Scripts/Tools/AuditDeadAssets.gd
# =========================================================

const OUT_REPORT := "user://dead_assets_report.txt"
const SOURCE_DIRS := ["Scripts", "Scenes", "Data", "Localization", "Set", "Maps"]
const TEXT_EXTS := [".cs", ".tscn", ".tres", ".gd", ".csv", ".json", ".godot", ".gdshader"]

func _initialize() -> void:
	# ---- 收集引用：按 basename 建集合（不拼接大字符串） ----
	var refs := {}
	var scanned := 0
	for d in SOURCE_DIRS:
		scanned += _collect_refs("res://" + d, refs)
	print("[Audit] 扫描 %d 个文本文件，收集 %d 个被引用的 basename" % [scanned, refs.size()])

	var lines: Array[String] = []
	lines.append("# ArtRes 死资源审计报告")
	lines.append("# 引用判定：basename 是否出现在 Scripts/Scenes/Data/Localization/Set/Maps 的文本里")
	lines.append("")

	var a := _audit_sidecars(refs, lines)
	var b := _audit_others(refs, lines)

	lines.append("")
	lines.append("## 汇总")
	lines.append("A. sidecar（疑似 GLB 内嵌贴图重复）: %d 个 / %.1f MB" % [a[0], a[1] / 1048576.0])
	lines.append("B. 其它无引用资源                 : %d 个 / %.1f MB" % [b[0], b[1] / 1048576.0])
	lines.append("合计可回收                       : %.1f MB" % [(a[1] + b[1]) / 1048576.0])

	var text := "\n".join(lines)
	var f := FileAccess.open(OUT_REPORT, FileAccess.WRITE)
	if f:
		f.store_string(text)
		f.close()
		print("[Audit] 报告: ", ProjectSettings.globalize_path(OUT_REPORT))
	print(text)
	quit(0)

# ---------------------------------------------------------
func _collect_refs(dir_path: String, refs: Dictionary) -> int:
	var n := 0
	var d := DirAccess.open(dir_path)
	if d == null: return 0
	d.list_dir_begin()
	var name := d.get_next()
	while name != "":
		var full := dir_path + "/" + name
		if d.current_is_dir():
			n += _collect_refs(full, refs)
		elif _is_text(name):
			var f := FileAccess.open(full, FileAccess.READ)
			if f != null:
				var s := f.get_as_text()
				f.close()
				# 逐个 token 收进来即可：我们只关心"某个文件名有没有出现过"
				for tok in s.split("\n", false):
					refs[tok.strip_edges()] = true
			n += 1
		name = d.get_next()
	d.list_dir_end()
	return n

func _is_text(n: String) -> bool:
	for e in TEXT_EXTS:
		if n.ends_with(e): return true
	return false

# 判定"basename 是否被引用"：直接在所有已收集行里找子串
func _referenced(refs: Dictionary, base: String) -> bool:
	for line in refs.keys():
		if line.contains(base):
			return true
	return false

# ---------------------------------------------------------
# A. sidecar 校验
# ---------------------------------------------------------
func _audit_sidecars(refs: Dictionary, lines: Array[String]) -> Array:
	lines.append("## A. `<Name>_0.png` sidecar")
	lines.append("判定：引用=全项目是否提到该文件名；内嵌=PNG 头部字节是否能在这个 GLB 里找到")

	var glbs: Array[String] = []
	_collect("res://ArtRes/models", ".glb", glbs)

	var count := 0
	var bytes := 0
	var suspicious := 0

	for glb in glbs:
		var png := glb.substr(0, glb.length() - 4) + "_0.png"
		if not FileAccess.file_exists(png):
			continue

		var size := _size(png)
		var referenced := _referenced(refs, png.get_file())
		var embedded := _embedded(png, glb)

		var verdict := "可删"
		if referenced:
			verdict = "★保留(有引用)"
		elif not embedded:
			verdict = "★人工确认(未找到内嵌)"
			suspicious += 1
		else:
			count += 1
			bytes += size

		lines.append("%-22s %s  引用=%s 内嵌=%s %.1fKB" % [
			verdict, png.replace("res://", ""), referenced, embedded, size / 1024.0])

	if suspicious > 0:
		lines.append("⚠ 有 %d 个未能确认内嵌，删除前需人工看" % suspicious)

	return [count, bytes]

# PNG 是否内嵌在 GLB 里：在 GLB 二进制中搜索 PNG 的前 96 字节。
# GLB 内嵌的就是同一个 PNG 字节流，头部（签名+IHDR+部分 IDAT 起点）足以判定同一张图。
func _embedded(png_path: String, glb_path: String) -> bool:
	var pf := FileAccess.open(png_path, FileAccess.READ)
	if pf == null: return false
	var head := pf.get_buffer(mini(96, pf.get_length()))
	pf.close()

	var gf := FileAccess.open(glb_path, FileAccess.READ)
	if gf == null: return false
	var blob := gf.get_buffer(gf.get_length())
	gf.close()

	return blob.find(head) >= 0

# ---------------------------------------------------------
# B. 其它无引用资源
# ---------------------------------------------------------
func _audit_others(refs: Dictionary, lines: Array[String]) -> Array:
	lines.append("")
	lines.append("## B. 其它无引用资源")

	var keep := {
		"Round Rover.glb": "包完整性哨兵（MainMenuController 用它判断包完整）",
		"License.txt": "CC0 许可声明",
		"Cannon_1.obj": "Tower 在用",
		"Cannon_1.mtl": "Tower 在用",
	}

	var files: Array[String] = []
	for ext in [".glb", ".obj", ".mtl", ".fbx", ".png"]:
		_collect("res://ArtRes/models", ext, files)

	var count := 0
	var bytes := 0
	for f in files:
		var base := f.get_file()
		if _referenced(refs, base):
			continue
		if keep.has(base):
			lines.append("保留   %s  （%s）" % [f.replace("res://", ""), keep[base]])
			continue
		var size := _size(f)
		count += 1
		bytes += size
		lines.append("可删   %s  %.1fKB" % [f.replace("res://", ""), size / 1024.0])

	return [count, bytes]

# ---------------------------------------------------------
func _collect(dir_path: String, ext: String, out: Array[String]) -> void:
	var d := DirAccess.open(dir_path)
	if d == null: return
	d.list_dir_begin()
	var name := d.get_next()
	while name != "":
		var full := dir_path + "/" + name
		if d.current_is_dir():
			_collect(full, ext, out)
		elif name.to_lower().ends_with(ext):
			out.append(full)
		name = d.get_next()
	d.list_dir_end()

func _size(path: String) -> int:
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null: return 0
	var n := f.get_length()
	f.close()
	return n
