# Input smoke test: feeds real mouse/keyboard events through Godot's Input into the main scene and
# checks selection (only your own units), group moves, capturing and flipping the switch, the hotseat
# swap with its per-side camera, and camera pan and zoom.
# Needs a window (headless Godot drops input events). From the repo root:
#   Godot_v4.7.2-stable_mono_win64_console.exe --path godot --fixed-fps 60 -s ../tools/input_smoke_test.gd
# Prints PASS/FAIL per check and exits with the number of failures.
extends SceneTree

const SWITCH := Vector3(9, 0.2, 0) # on the prototype map

var frame := 0
var failures := 0
var cam: Camera3D
var blue: Array
var red: Array
var arrow: Node3D
var arrow_before: Vector3
var cam_before: Vector3

func _initialize():
	change_scene_to_file("res://scenes/main.tscn")

func _process(_delta) -> bool:
	frame += 1
	match frame:
		10:
			cam = root.get_node("Main/Camera")
			cam.set("EdgeScroll", false) # the real cursor could sit at a screen edge
			var views = root.get_node("Main/UnitsView").get_children().filter(func(n): return n.has_node("SelectionRing"))
			blue = views.filter(func(v): return v.get("PlayerIndex") == 0)
			red = views.filter(func(v): return v.get("PlayerIndex") == 1)
			check("four units per side", [blue.size(), red.size()], [4, 4])
		20:
			box(blue)
		25:
			check("box selects all of your units", selected(blue), 4)
			right_click(cam.unproject_position(Vector3.ZERO))
		520:
			# Four units around (0, 0, 0) in a 2x2 grid with 3 m spacing. Squads stop exactly on their slot;
			# vehicles brake to a stop within about 0.6 m of it.
			var slots := [Vector3(-1.5, 0, -1.5), Vector3(1.5, 0, -1.5), Vector3(-1.5, 0, 1.5), Vector3(1.5, 0, 1.5)]
			for i in blue.size():
				check("unit %d at its formation slot" % i, blue[i].global_position.distance_to(slots[i]) < 1.0, true)
			click(screen(blue[0]))
		525:
			check("click selects one", selected(blue), 1)
			key(KEY_SHIFT, true)
			click(screen(blue[1]))
		530:
			check("shift-click adds", selected(blue), 2)
			click(screen(blue[1]))
		535:
			check("shift-click toggles off", selected(blue), 1)
			key(KEY_SHIFT, false)
			click(screen(red[0]))
		540:
			check("enemy units can't be selected", [selected(blue), selected(red)], [0, 0])
			box(blue)
		545:
			right_click(cam.unproject_position(SWITCH))
		1120:
			# Walked over (~2 s) and held it uncontested (5 s): it's Blue's, but still closed.
			arrow = switch_arrow()
			check("switch arrow exists and is hidden while closed", arrow != null and not arrow.visible, true)
			click(cam.unproject_position(SWITCH))
		1125:
			check("clicking your switch opens it", arrow.visible, true)
			arrow_before = arrow.global_position
			click(cam.unproject_position(SWITCH))
		1130:
			# Both outputs leave heading east; south bends toward +z, so the arrow moves that way.
			check("clicking it again flips it north -> south", arrow.global_position.z > arrow_before.z + 0.01, true)
			blue_view = cam.get("Focus")
			key(KEY_F2, true)
			key(KEY_F2, false)
		1148:
			# 0.3 s into a 0.6 s flight: partway, not jumped.
			var focus: Vector2 = cam.get("Focus")
			check("F2 glides the camera instead of jumping", focus.distance_to(blue_view) > 0.5 and focus.distance_to(RED_HOME) > 0.5, true)
		1180:
			check("F2 lands on Red's spawn the first time", (cam.get("Focus") as Vector2).distance_to(RED_HOME) < 0.1, true)
			box(red)
		1185:
			check("F2 swaps to Red: Red's units select, Blue's don't", [selected(red), selected(blue)], [4, 0])
			key(KEY_F2, true)
			key(KEY_F2, false)
		1235:
			check("F2 back returns to where Blue left off", (cam.get("Focus") as Vector2).distance_to(blue_view) < 0.1, true)
			cam_before = cam.global_position
			key(KEY_RIGHT, true)
		1265:
			key(KEY_RIGHT, false)
			check("arrow key pans the camera", cam.global_position.x > cam_before.x + 5, true)
			cam_before = cam.global_position
			mouse(MOUSE_BUTTON_WHEEL_UP, Vector2(576, 324), true)
			mouse(MOUSE_BUTTON_WHEEL_UP, Vector2(576, 324), false)
		1325:
			check("wheel zooms in", cam.global_position.y < cam_before.y - 3, true)
			cam_before = cam.global_position
			mouse(MOUSE_BUTTON_MIDDLE, Vector2(576, 324), true)
			motion(Vector2(676, 324))
			mouse(MOUSE_BUTTON_MIDDLE, Vector2(676, 324), false)
		1330:
			check("middle-mouse drag moves the map with the cursor", cam.global_position.x < cam_before.x - 1, true)
			print("DONE: %d failure(s)" % failures)
			quit(failures)
	return false

const RED_HOME := Vector2(10, 12.375) # the middle of Red's four unit spawns
var blue_view: Vector2

func check(name: String, actual, expected):
	var ok: bool = actual == expected
	if not ok:
		failures += 1
	print("%s  %s (got %s, want %s)" % ["PASS" if ok else "FAIL", name, actual, expected])

func selected(views: Array) -> int:
	return views.filter(func(v): return is_instance_valid(v) and v.get_node("SelectionRing").visible).size()

func screen(v: Node3D) -> Vector2:
	return cam.unproject_position(v.global_position + Vector3(0, 0.6, 0))

# BeltView draws each junction's arrow as a 0.35 x 0.15 x 1 box; only switches ever show it.
func switch_arrow() -> Node3D:
	var j := 0
	for n in root.get_node("Main/BeltView").get_children():
		if n is MeshInstance3D and n.mesh is BoxMesh and n.mesh.size.is_equal_approx(Vector3(0.35, 0.15, 1)):
			j += 1
			if j == 2: # junctions in scene order: Merge, then Switch
				return n
	return null

func box(views: Array):
	var lo := Vector2(INF, INF)
	var hi := Vector2(-INF, -INF)
	for v in views:
		lo = lo.min(screen(v))
		hi = hi.max(screen(v))
	mouse(MOUSE_BUTTON_LEFT, lo - Vector2(40, 40), true)
	motion(hi + Vector2(40, 40))
	mouse(MOUSE_BUTTON_LEFT, hi + Vector2(40, 40), false)

func mouse(button: int, pos: Vector2, pressed: bool):
	var e := InputEventMouseButton.new()
	e.button_index = button
	e.position = pos
	e.global_position = pos
	e.pressed = pressed
	Input.parse_input_event(e)

func motion(pos: Vector2):
	var m := InputEventMouseMotion.new()
	m.position = pos
	m.global_position = pos
	Input.parse_input_event(m)

func click(pos: Vector2):
	mouse(MOUSE_BUTTON_LEFT, pos, true)
	mouse(MOUSE_BUTTON_LEFT, pos, false)

func right_click(pos: Vector2):
	mouse(MOUSE_BUTTON_RIGHT, pos, true)
	mouse(MOUSE_BUTTON_RIGHT, pos, false)

func key(code: int, pressed: bool):
	var e := InputEventKey.new()
	e.keycode = code
	e.pressed = pressed
	Input.parse_input_event(e)
