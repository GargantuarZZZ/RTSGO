extends Node
## 三视图渲染工具：从 GLB 网格正交投影渲染 前/侧/顶 三张图。
## 用法：Godot --path . --resolution 512x512 res://Scripts/Tools/RenderTriView.tscn -- <glb路径> <输出目录>

const DEFAULT_GLB := "D:/DEV/AI3D-Pipeline/outputs/001_a_demonic_war_dog_black_metal_armor_plates_glowi/white_mesh.glb"
const DEFAULT_OUT := "D:/DEV/AI3D-Pipeline/outputs/001_a_demonic_war_dog_black_metal_armor_plates_glowi/views"
const ORTHO_SIZE := 3.6

var _cam: Camera3D
var _total_verts := 0
var _total_tris := 0

func _ready() -> void:
	var args := OS.get_cmdline_user_args()
	var glb_path: String = args[0] if args.size() > 0 else DEFAULT_GLB
	var out_dir: String = args[1] if args.size() > 1 else DEFAULT_OUT
	var align_mode: String = args[2] if args.size() > 2 else "long"

	RenderingServer.set_default_clear_color(Color(0.88, 0.88, 0.90, 1.0))

	if DirAccess.dir_exists_absolute(glb_path):
		var files := _collect_glbs(glb_path)
		if files.is_empty():
			printerr("TRIVIEW no glb found under: ", glb_path)
			get_tree().quit(1)
			return
		for i in files.size():
			var glb := files[i]
			var rel := glb.trim_prefix(glb_path.trim_suffix("/") + "/")
			var sub_out := out_dir + "/" + rel.get_base_dir()
			print("TRIVIEW [", i + 1, "/", files.size(), "] ", glb, " -> ", sub_out)
			await _render_glb(glb, sub_out, align_mode)
		print("TRIVIEW_DONE count=", files.size())
		get_tree().quit(0)
		return

	await _render_glb(glb_path, out_dir, align_mode)
	print("TRIVIEW_DONE")
	get_tree().quit(0)

func _collect_glbs(dir_path: String) -> PackedStringArray:
	var out := PackedStringArray()
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return out
	dir.list_dir_begin()
	var name := dir.get_next()
	while name != "":
		if not name.begins_with(".") and dir.current_is_dir():
			var sub := dir_path + "/" + name
			var subdir := DirAccess.open(sub)
			if subdir != null:
				subdir.list_dir_begin()
				var n2 := subdir.get_next()
				while n2 != "":
					if n2 == "textured_mesh.glb" or (n2.ends_with(".glb") and not n2.begins_with("white_mesh")):
						out.append(sub + "/" + n2)
					n2 = subdir.get_next()
				subdir.list_dir_end()
		name = dir.get_next()
	dir.list_dir_end()
	out.sort()
	return out

func _render_glb(glb_path: String, out_dir: String, align_mode: String) -> void:
	DirAccess.make_dir_recursive_absolute(out_dir)
	var doc := GLTFDocument.new()
	var state := GLTFState.new()
	var err := doc.append_from_file(glb_path, state)
	if err != OK:
		printerr("TRIVIEW load fail: ", err)
		return

	var scene := doc.generate_scene(state)
	if scene == null:
		printerr("TRIVIEW scene null")
		return
	add_child(scene)
	_align_model(scene, align_mode)

	_cam = Camera3D.new()
	_cam.projection = Camera3D.PROJECTION_ORTHOGONAL
	_cam.size = ORTHO_SIZE
	_cam.near = 0.01
	_cam.far = 100.0
	add_child(_cam)
	_cam.current = true

	var key := DirectionalLight3D.new()
	key.rotation_degrees = Vector3(-40, -30, 0)
	key.light_energy = 1.4
	add_child(key)

	var fill := DirectionalLight3D.new()
	fill.rotation_degrees = Vector3(20, 140, 0)
	fill.light_energy = 0.6
	add_child(fill)

	await get_tree().process_frame
	await get_tree().process_frame

	await _render("ortho_front", Vector3(0, 0, 8), false, out_dir)
	await _render("ortho_side", Vector3(8, 0, 0), false, out_dir)
	await _render("ortho_top", Vector3(0, 8, 0), true, out_dir)
	scene.queue_free()
	_cam.queue_free()

func _render(file_name: String, cam_pos: Vector3, top_view: bool, out_dir: String) -> void:
	_cam.global_position = cam_pos
	if top_view:
		_cam.global_transform.basis = Basis(Vector3.RIGHT, deg_to_rad(-90.0))
	else:
		_cam.look_at(Vector3.ZERO, Vector3.UP)

	await get_tree().process_frame
	await get_tree().process_frame

	var img := get_viewport().get_texture().get_image()
	var path := out_dir + "/" + file_name + ".png"
	var save_err := img.save_png(path)
	print("TRIVIEW saved=", path, " err=", save_err)

# 水平主轴向校正：用网格顶点在 XZ 平面的 PCA 求身体主轴。
# long = 长轴对齐 Z（地面单位：正面看到头/尾）；short = 短轴对齐 Z（飞行单位：翅膀张开沿 X）；
# none = 不校正。
func _align_model(root: Node3D, mode: String) -> void:
	if mode == "none":
		print("TRIVIEW align skipped (mode=none)")
		return

	var verts := PackedVector3Array()
	_collect_vertices(root, verts)
	print("TRIVIEW mesh verts=", _total_verts, " tris=", _total_tris)
	if verts.size() < 8:
		print("TRIVIEW align skipped (too few verts)")
		return

	var cx := 0.0
	var cz := 0.0
	for p in verts:
		cx += p.x
		cz += p.z
	cx /= verts.size()
	cz /= verts.size()

	var xx := 0.0
	var xz := 0.0
	var zz := 0.0
	for p in verts:
		var dx := p.x - cx
		var dz := p.z - cz
		xx += dx * dx
		xz += dx * dz
		zz += dz * dz

	# 2x2 对称协方差的最大特征值方向
	var disc := sqrt((xx - zz) * (xx - zz) + 4.0 * xz * xz)
	var lmax := (xx + zz + disc) * 0.5
	var vx := 1.0
	var vz := 0.0
	if abs(xz) > 1e-9 or abs(lmax - xx) > 1e-9:
		vx = xz
		vz = lmax - xx

	var len := sqrt(vx * vx + vz * vz)
	if len < 1e-6:
		print("TRIVIEW align skipped (degenerate)")
		return

	vx /= len
	vz /= len
	var angle := atan2(vx, vz)
	if mode == "short":
		root.rotate_y(PI / 2.0 - angle)
		print("TRIVIEW align mode=short angle=", snappedf(rad_to_deg(angle), 0.1), "deg")
	else:
		root.rotate_y(-angle)
		print("TRIVIEW align mode=long angle=", snappedf(rad_to_deg(angle), 0.1), "deg")

func _collect_vertices(node: Node, verts: PackedVector3Array) -> void:
	if node is MeshInstance3D:
		var mi := node as MeshInstance3D
		if mi.mesh != null:
			for s in range(mi.mesh.get_surface_count()):
				var arrays := mi.mesh.surface_get_arrays(s)
				if arrays.size() > Mesh.ARRAY_VERTEX:
					var v := arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array
					for p in v:
						verts.append(mi.global_transform * p)
						_total_verts += 1
				if arrays.size() > Mesh.ARRAY_INDEX:
					var idx := arrays[Mesh.ARRAY_INDEX] as PackedInt32Array
					_total_tris += idx.size() / 3
	for child in node.get_children():
		_collect_vertices(child, verts)
