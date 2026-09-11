extends Node
## VAT 烘焙工具：把带骨骼动画的 GLB 烘焙成顶点/法线动画纹理（可在编辑器外运行）。
## 运行：Godot --path . res://Scripts/Tools/VatBakeTool.tscn

const SHADER_PATH := "res://addons/animated_multimeshinstance3d/shaders/vetex_animation_shader.gdshader"
const SAMPLING_FPS := 10.0
const VERTEX_LIMIT := 8192
const FRAME_LIMIT := 8192

const MODELS := [
	{
		"model": "res://ArtRes/models/Astronaut.glb",
		"idle": "Idle_Gun",
		"run": "Run_Gun",
	},
	{
		"model": "res://ArtRes/models/Astronaut-0D54W8yfrA.glb",
		"idle": "Idle_Gun",
		"run": "Run_Gun",
	},
	{
		"model": "res://ArtRes/models/Astronaut-OgeSH89Nmx.glb",
		"idle": "Idle_Gun",
		"run": "Run_Gun",
	},
	{
		"model": "res://ArtRes/models/Mech.glb",
		"idle": "Idle",
		"run": "Run",
	},
	{
		"model": "res://ArtRes/models/Mech-4UvIHxnoSR.glb",
		"idle": "Idle",
		"run": "Run",
	},
	{
		"model": "res://ArtRes/models/Mech-D5wW2jDO42.glb",
		"idle": "Idle",
		"run": "Run",
	},
	{
		"model": "res://ArtRes/models/Mech-o3Ps8z8ByP.glb",
		"idle": "Idle",
		"run": "Run",
	},
	{
		"model": "res://ArtRes/models/Enemy Large.glb",
		"idle": "Idle",
		"run": "Run",
	},
	{
		"model": "res://ArtRes/models/Enemy Small.glb",
		"idle": "Flying_Idle",
		"run": "Fast_Flying",
	},
	{
		"model": "res://ArtRes/models/Enemy Flying.glb",
		"idle": "Flying_Idle",
		"run": "Fast_Flying",
	},
]

func _ready() -> void:
	for cfg in MODELS:
		await _bake_model(cfg)
	print("VAT_BAKE_ALL_DONE")
	get_tree().quit(0)

func _bake_model(cfg: Dictionary) -> void:
	var model_path: String = cfg.model
	var packed: PackedScene = load(model_path)
	if packed == null:
		printerr("VAT_BAKE load fail: ", model_path)
		return

	var root := packed.instantiate() as Node3D
	add_child(root)
	await get_tree().process_frame
	await get_tree().process_frame

	var player := _find_animation_player(root)
	var mesh_inst := _find_skinned_mesh(root)
	if player == null or mesh_inst == null:
		printerr("VAT_BAKE no player/mesh: ", model_path)
		root.queue_free()
		return

	var available := player.get_animation_list()
	print("VAT_BAKE ", model_path, " animations=", available)

	var names: Array[String] = []
	for wanted in [cfg.get("idle", ""), cfg.get("run", "")]:
		if wanted == "":
			continue
		var found := ""
		for name in available:
			if String(name).contains(wanted):
				found = String(name)
				break
		if found != "" and not names.has(found):
			names.append(found)
	if names.is_empty() and available.size() > 0:
		names.append(String(available[0]))

	var source_mesh := mesh_inst.mesh as ArrayMesh
	if source_mesh == null or source_mesh.get_surface_count() == 0:
		printerr("VAT_BAKE mesh invalid: ", model_path)
		root.queue_free()
		return
	var mdt0 := MeshDataTool.new()
	mdt0.create_from_surface(source_mesh, 0)
	var vertex_count := mdt0.get_vertex_count()
	if vertex_count > VERTEX_LIMIT:
		printerr("VAT_BAKE vertex limit exceeded: ", vertex_count)
		root.queue_free()
		return

	var total_frames := 0
	for name in names:
		total_frames += maxi(ceili(player.get_animation(name).length * SAMPLING_FPS), 1)
	if total_frames > FRAME_LIMIT:
		printerr("VAT_BAKE frame limit exceeded: ", total_frames)
		root.queue_free()
		return

	var vimg := Image.create_empty(total_frames, vertex_count, false, Image.FORMAT_RGBF)
	var nimg := Image.create_empty(total_frames, vertex_count, false, Image.FORMAT_RGBF)
	var anim_data := {}
	var frame_counter := 0
	var global_min := Vector3(INF, INF, INF)
	var global_max := Vector3(-INF, -INF, -INF)
	# 把网格局部空间变换到 GLB 根空间（含骨架 100 倍缩放与 -90° 旋转），
	# 这样烘焙纹理就是“骨架空间/根空间”坐标，运行时只需单位自身的 scale/offset。
	var rel := root.global_transform.affine_inverse() * mesh_inst.global_transform

	for name in names:
		var anim: Animation = player.get_animation(name)
		var frames := maxi(ceili(anim.length * SAMPLING_FPS), 1)
		player.play(name)
		player.advance(1.0 / SAMPLING_FPS)
		for f in frames:
			var baked := mesh_inst.bake_mesh_from_current_skeleton_pose() as ArrayMesh
			var mdt := MeshDataTool.new()
			mdt.create_from_surface(baked, 0)
			if mdt.get_vertex_count() != vertex_count:
				printerr("VAT_BAKE vertex count mismatch: ", mdt.get_vertex_count(), " vs ", vertex_count)
				root.queue_free()
				return
			for i in vertex_count:
				var v := rel * mdt.get_vertex(i)
				var n := rel.basis * mdt.get_vertex_normal(i)
				vimg.set_pixel(frame_counter, i, Color(v.x, v.y, v.z))
				nimg.set_pixel(frame_counter, i, Color(n.x, n.y, n.z))
				global_min = global_min.min(v)
				global_max = global_max.max(v)
			frame_counter += 1
			player.advance(1.0 / SAMPLING_FPS)
			await get_tree().process_frame
		anim_data[name] = {"start": frame_counter - frames, "length": frames}
		print("VAT_BAKE anim=", name, " frames=", frames, " start=", frame_counter - frames)

	var out_dir := "res://Data/VAT/" + model_path.get_file().get_basename()
	DirAccess.make_dir_recursive_absolute(out_dir)

	var vtex := ImageTexture.create_from_image(vimg)
	var ntex := ImageTexture.create_from_image(nimg)
	var err_v := ResourceSaver.save(vtex, out_dir + "/vertex_animation.tres")
	var err_n := ResourceSaver.save(ntex, out_dir + "/normal_animation.tres")
	if err_v != Error.OK or err_n != Error.OK:
		printerr("VAT_BAKE texture save fail: ", err_v, " ", err_n)
		root.queue_free()
		return

	var mat := ShaderMaterial.new()
	mat.shader = load(SHADER_PATH)
	mat.set_shader_parameter("vertex_animation", load(out_dir + "/vertex_animation.tres"))
	mat.set_shader_parameter("normal_animation", load(out_dir + "/normal_animation.tres"))
	mat.set_shader_parameter("total_frame_count", float(total_frames))
	mat.set_shader_parameter("total_vertex_count", float(vertex_count))
	mat.set_shader_parameter("sampling_fps", SAMPLING_FPS)
	var src_mat := mesh_inst.get_active_material(0) as BaseMaterial3D
	if src_mat != null and src_mat.albedo_texture != null:
		mat.set_shader_parameter("albedo", src_mat.albedo_texture)
	ResourceSaver.save(mat, out_dir + "/animation_material.tres")

	var f := FileAccess.open(out_dir + "/animations.json", FileAccess.WRITE)
	f.store_string(JSON.stringify(anim_data))
	f.close()

	print("VAT_BAKE saved=", out_dir, " frames=", total_frames, " verts=", vertex_count,
		" bakedMin=", global_min, " bakedMax=", global_max, " bakedSize=", global_max - global_min)
	root.queue_free()

func _find_animation_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer:
		return node
	for child in node.get_children():
		var found := _find_animation_player(child)
		if found != null:
			return found
	return null

func _find_skinned_mesh(node: Node) -> MeshInstance3D:
	if node is MeshInstance3D:
		var mi := node as MeshInstance3D
		if mi.skin != null and mi.mesh != null:
			return mi
	for child in node.get_children():
		var found := _find_skinned_mesh(child)
		if found != null:
			return found
	return null
