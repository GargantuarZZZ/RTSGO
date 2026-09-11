extends SceneTree

func _init():
	var ps = load("res://ArtRes/models/Astronaut.glb")
	if ps == null:
		print("LOAD FAIL")
		quit()
		return
	var root = ps.instantiate()
	_walk(root, "")
	quit()

func _walk(n, ind):
	var t = "n/a"
	if n is Node3D:
		t = str(n.transform)
	var m = ""
	if n is MeshInstance3D:
		m = " AABB=" + str(n.mesh.get_aabb())
	print(ind, n.name, " [", n.get_class(), "] t=", t, m)
	for c in n.get_children():
		_walk(c, ind + "  ")
