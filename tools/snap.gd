# Saves chosen frames of a scene as PNGs, much faster than --write-movie: only those frames are
# written, and with vsync off the game runs as fast as it renders. Needs a window (headless draws
# nothing). Run from the repo root:
#
#   godot --path godot --fixed-fps 60 --disable-vsync -s ../tools/snap.gd -- \
#       --scene=res://scenes/prototype.tscn --frames=600,606,1040 --out=<dir> [--demo] \
#       [--camera=x,z,distance] [--set=SimHost.SpillLoss=1]
#
# --fixed-fps keeps every frame the same step of game time, so frame N is the same moment on every run.
# --camera puts the camera's focus (ground x, z) and distance before the scene starts; --set sets a
# node's property (float) before it starts. Frames are saved as <out>/f<frame>.png.
extends SceneTree

var frames: Array[int] = []
var out := ""
var frame := 0


func _initialize():
	var scene_path := "res://scenes/prototype.tscn"
	var camera := PackedFloat64Array()
	var sets: Array[String] = []
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--scene="): scene_path = arg.trim_prefix("--scene=")
		elif arg.begins_with("--frames="):
			for f in arg.trim_prefix("--frames=").split(","): frames.append(int(f))
		elif arg.begins_with("--out="): out = arg.trim_prefix("--out=")
		elif arg.begins_with("--camera="): camera = arg.trim_prefix("--camera=").split_floats(",")
		elif arg.begins_with("--set="): sets.append(arg.trim_prefix("--set="))
	if frames.is_empty() or out == "":
		push_error("snap.gd needs --frames=... and --out=...")
		quit(1)
		return
	frames.sort()
	DirAccess.make_dir_recursive_absolute(out)

	var scene: Node = load(scene_path).instantiate()
	if camera.size() == 3:
		var cam := scene.get_node("Camera")
		cam.set("EdgeScroll", false)
		cam.set("MinDistance", min(camera[2], cam.get("MinDistance")))
		cam.set("Focus", Vector2(camera[0], camera[1]))
		cam.set("Distance", camera[2])
	for s in sets: # Node.Property=value
		var node_prop := s.get_slice("=", 0)
		scene.get_node(node_prop.get_slice(".", 0)).set(node_prop.get_slice(".", 1), float(s.get_slice("=", 1)))
	root.add_child(scene)
	current_scene = scene


func _process(_delta: float) -> bool:
	frame += 1
	if frame in frames:
		# What was drawn last frame; close enough, and the same on every run.
		root.get_texture().get_image().save_png("%s/f%05d.png" % [out, frame])
	return frame >= frames[-1]
