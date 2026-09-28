# Input smoke test: feeds real mouse/keyboard events through Godot's Input into the main scene and
# checks box select, click select, shift add/toggle, deselect, and a group move.
# Needs a window (headless Godot drops input events). From the repo root:
#   Godot_v4.7.2-stable_mono_win64_console.exe --path godot --fixed-fps 60 -s ../tools/input_smoke_test.gd
# Prints PASS/FAIL per check and exits with the number of failures.
extends SceneTree

var frame := 0
var failures := 0
var cam: Camera3D
var views: Array

func _initialize():
	change_scene_to_file("res://scenes/main.tscn")

func _process(_delta) -> bool:
	frame += 1
	match frame:
		10:
			cam = root.get_node("Main/Camera")
			views = root.get_node("Main/UnitsView").get_children().filter(func(n): return n.has_node("SelectionRing"))
			check("three units", views.size(), 3)
		20:
			var lo := Vector2(INF, INF)
			var hi := Vector2(-INF, -INF)
			for v in views:
				lo = lo.min(screen(v))
				hi = hi.max(screen(v))
			drag(lo - Vector2(30, 30), hi + Vector2(30, 30))
		25:
			check("box selects all", selected(), 3)
			right_click(cam.unproject_position(Vector3.ZERO))
		300:
			# Three units around (0, 0, 0) in a 2x2 grid with 2 m spacing.
			var expected := [Vector3(-1, 0, -1), Vector3(1, 0, -1), Vector3(-1, 0, 1)]
			for i in views.size():
				check("unit %d at its formation slot" % i, views[i].global_position.distance_to(expected[i]) < 0.05, true)
			click(screen(views[0]))
		305:
			check("click selects one", selected(), 1)
			key(KEY_SHIFT, true)
			click(screen(views[1]))
		310:
			check("shift-click adds", selected(), 2)
			click(screen(views[1]))
		315:
			check("shift-click toggles off", selected(), 1)
			key(KEY_SHIFT, false)
			click(cam.unproject_position(Vector3(15, 0, 10)))
		320:
			check("click on empty ground clears", selected(), 0)
			print("DONE: %d failure(s)" % failures)
			quit(failures)
	return false

func check(name: String, actual, expected):
	var ok: bool = actual == expected
	if not ok:
		failures += 1
	print("%s  %s (got %s, want %s)" % ["PASS" if ok else "FAIL", name, actual, expected])

func selected() -> int:
	return views.filter(func(v): return v.get_node("SelectionRing").visible).size()

func screen(v: Node3D) -> Vector2:
	return cam.unproject_position(v.global_position + Vector3(0, 0.5, 0))

func mouse(button: int, pos: Vector2, pressed: bool):
	var e := InputEventMouseButton.new()
	e.button_index = button
	e.position = pos
	e.global_position = pos
	e.pressed = pressed
	Input.parse_input_event(e)

func click(pos: Vector2):
	mouse(MOUSE_BUTTON_LEFT, pos, true)
	mouse(MOUSE_BUTTON_LEFT, pos, false)

func right_click(pos: Vector2):
	mouse(MOUSE_BUTTON_RIGHT, pos, true)
	mouse(MOUSE_BUTTON_RIGHT, pos, false)

func drag(from: Vector2, to: Vector2):
	mouse(MOUSE_BUTTON_LEFT, from, true)
	var m := InputEventMouseMotion.new()
	m.position = to
	m.global_position = to
	Input.parse_input_event(m)
	mouse(MOUSE_BUTTON_LEFT, to, false)

func key(code: int, pressed: bool):
	var e := InputEventKey.new()
	e.keycode = code
	e.pressed = pressed
	Input.parse_input_event(e)
