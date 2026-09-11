extends SceneTree

# 诊断：把 StandardMaterial3D 里所有和 UV/三平面相关的属性名打出来。
# 用途：Godot 版本之间属性命名会变（例如世界三平面的尺度属性），
# 猜名字会导致编译失败，直接问引擎最快。
func _initialize() -> void:
	var m := StandardMaterial3D.new()
	var props := m.get_property_list()
	print("[MatProps] 与 uv/triplanar 相关的属性：")
	for p in props:
		var n: String = p["name"]
		if n.contains("uv1") or n.contains("triplanar") or n.contains("uv2"):
			print("  ", n, "  (type=", p["type"], ")")
	quit(0)
