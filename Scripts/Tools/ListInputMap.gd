extends SceneTree

# 诊断：打印 InputMap 里 game_* / panel_* 动作实际绑定的键。
# 用途：确认 GameSettings.ApplyKeybinds 真的把物理键写进了 InputMap，
# 以及是否存在两个动作抢同一个键。
#
# 运行：godot --headless --script res://Scripts/Tools/ListInputMap.gd --path .
# 注意：这个脚本用的是引擎启动时的默认 InputMap（项目设置里没定义任何动作），
# 所以必须先由 C# 侧调用 ApplyKeybinds。这里改为直接读 GameSettings 的静态表
# 会更绕，因此本脚本只作为"引擎默认动作"的对照，实际校验靠运行期 ART_DEBUG。

func _initialize() -> void:
	print("[InputMap] 引擎内置动作抽样:")
	for name in ["ui_cancel", "ui_accept", "ui_left", "ui_right", "ui_up", "ui_down"]:
		if not InputMap.has_action(name):
			print("  ", name, " : <不存在>")
			continue
		var keys: Array[String] = []
		for e in InputMap.action_get_events(name):
			if e is InputEventKey:
				keys.append(OS.get_keycode_string((e as InputEventKey).physical_keycode))
		print("  %s : %s" % [name, ", ".join(keys)])
	quit(0)
