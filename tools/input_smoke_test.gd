# Input smoke test: feeds real mouse/keyboard events through Godot's Input into the main scene and
# checks selection (only your own units), group moves, the switch (splitting while neutral, turning to
# your post when you capture it, flipping when you click it), the hotseat swap with its per-side camera,
# double-click select by type, the cursor, attack-move, order paths (only for the selection, colored by
# order), camera pan and zoom, and production (select the HQ, queue by hotkey, rally point, cancel).
# Needs a window (headless Godot drops input events). From the repo root:
#   Godot_v4.7.2-stable_mono_win64_console.exe --path godot --fixed-fps 60 -s ../tools/input_smoke_test.gd
# Prints PASS/FAIL per check and exits with the number of failures.
extends SceneTree

const SWITCH := Vector3(9, 0.2, 0) # on the prototype map

var frame := 0
var failures := 0
var cam: Camera3D
var player: Node
var blue: Array
var red: Array
var cam_before: Vector3

func _initialize():
	change_scene_to_file("res://scenes/main.tscn")

func _process(_delta) -> bool:
	frame += 1
	match frame:
		10:
			cam = root.get_node("Main/Camera")
			player = root.get_node("Main/PlayerInput")
			cam.set("EdgeScroll", false) # the real cursor could sit at a screen edge
			var views = root.get_node("Main/UnitsView").get_children().filter(func(n): return n.has_node("SelectionRing"))
			blue = views.filter(func(v): return v.get("PlayerIndex") == 0)
			red = views.filter(func(v): return v.get("PlayerIndex") == 1)
			check("four units per side", [blue.size(), red.size()], [4, 4])
		20:
			box(blue)
		25:
			check("box selects all of your units", selected(blue), 4)
			check("the neutral switch splits: arrows on both outputs", arrows_shown(), [true, true])
			right_click(cam.unproject_position(Vector3.ZERO))
		560:
			# Four units around (0, 0, 0) in a 2x2 grid with 3 m spacing. Squads stop exactly on their slot;
			# vehicles ease to a stop within about 0.6 m of it.
			var slots := [Vector3(-1.5, 0, -1.5), Vector3(1.5, 0, -1.5), Vector3(-1.5, 0, 1.5), Vector3(1.5, 0, 1.5)]
			for i in blue.size():
				check("unit %d at its formation slot" % i, blue[i].global_position.distance_to(slots[i]) < 1.0, true)
			click(screen(blue[0]))
		565:
			check("click selects one", selected(blue), 1)
			key(KEY_SHIFT, true)
			click(screen(blue[1]))
		570:
			check("shift-click adds", selected(blue), 2)
			click(screen(blue[1]))
		575:
			check("shift-click toggles off", selected(blue), 1)
			key(KEY_SHIFT, false)
			click(screen(red[0]))
		580:
			check("enemy units can't be selected", [selected(blue), selected(red)], [0, 0])
			box(blue)
		585:
			right_click(cam.unproject_position(SWITCH))
		1120:
			# Walked over (~2 s) and held it uncontested (5 s): it's Blue's, and it turned to Blue's post.
			check("capturing the switch turns it to your post (north)", arrows_shown(), [true, false])
			click(cam.unproject_position(SWITCH))
		1125:
			check("clicking your switch flips it south", arrows_shown(), [false, true])
			click(cam.unproject_position(SWITCH))
		1130:
			check("and back north", arrows_shown(), [true, false])
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
			double_click(screen(blue[0]))
		1240:
			# blue[0] and blue[1] are the squads, blue[2] and blue[3] the vehicles.
			check("double-click selects every squad on screen, not the vehicles", rings(blue), [true, true, false, false])
			box(blue)
		1245:
			motion(screen(red[0]))
		1250:
			check("cursor over an enemy: attack", player.get("CursorName"), "Attack")
			motion(cam.unproject_position(Vector3(0, 0, -10)))
		1255:
			check("cursor over open ground: move", player.get("CursorName"), "Move")
			motion(cam.unproject_position(SWITCH))
		1260:
			check("cursor over your own switch: flip", player.get("CursorName"), "Flip")
			key(KEY_A, true)
			key(KEY_A, false)
		1265:
			check("A arms attack-move", player.get("CursorName"), "AttackMove")
			click(cam.unproject_position(Vector3(3, 0, 11))) # short of Red's units
		1270:
			check("the click attack-moves and disarms (back to the plain cursor over open ground)", player.get("CursorName"), "Move")
			check("selected units show their order paths, orange for attack-move", path_colors().any(func(c): return absf(c.r - ORANGE.r) + absf(c.g - ORANGE.g) + absf(c.b - ORANGE.b) < 0.02), true)
			cam_before = cam.global_position
			key(KEY_RIGHT, true)
		1285:
			click(cam.unproject_position(Vector3(-20, 0, 8))) # empty ground: deselects
		1290:
			check("unselected units show no order paths", path_colors().size(), 0)
		1300:
			key(KEY_RIGHT, false)
			check("arrow key pans the camera", cam.global_position.x > cam_before.x + 5, true)
			cam_before = cam.global_position
			mouse(MOUSE_BUTTON_WHEEL_UP, Vector2(576, 324), true)
			mouse(MOUSE_BUTTON_WHEEL_UP, Vector2(576, 324), false)
		1360:
			check("wheel zooms in", cam.global_position.y < cam_before.y - 3, true)
			cam_before = cam.global_position
			mouse(MOUSE_BUTTON_MIDDLE, Vector2(576, 324), true)
			motion(Vector2(676, 324))
			mouse(MOUSE_BUTTON_MIDDLE, Vector2(676, 324), false)
		1365:
			check("middle-mouse drag moves the map with the cursor", cam.global_position.x < cam_before.x - 1, true)
		1600:
			# Six seconds after the attack-move: Blue walked into range of Red's units and opened fire.
			check("attack-move engages enemies on the way", red.any(func(v): return not is_instance_valid(v) or hurt(v)), true)
			cam.set("Focus", Vector2(-10, -17)) # over Blue's HQ
		1610:
			click(cam.unproject_position(BLUE_HQ))
		1615:
			var panel = root.get_node("Main/Ui/ProductionPanel")
			check("clicking your HQ selects it and shows production", [player.get("SelectedBuilding") != -1, panel.visible], [true, true])
			key(KEY_Q, true)
			key(KEY_Q, false)
		1620:
			check("Q queues the first unit type", root.get_node("Main/Ui/ProductionPanel").get("QueueLength"), 1)
			right_click(cam.unproject_position(RALLY))
		1625:
			var flag: Vector3 = root.get_node("Main/BuildingsView/Rally").global_position
			check("right-click with the HQ selected sets its rally point", flag.distance_to(RALLY) < 0.3, true)
			key(KEY_BACKSPACE, true)
			key(KEY_BACKSPACE, false)
		1630:
			check("Backspace cancels the last queued unit", root.get_node("Main/Ui/ProductionPanel").get("QueueLength"), 0)
			click(cam.unproject_position(Vector3(-20, 0, -8))) # empty ground
		1635:
			check("clicking away deselects the HQ and hides production", [player.get("SelectedBuilding"), root.get_node("Main/Ui/ProductionPanel").visible], [-1, false])
			print("DONE: %d failure(s)" % failures)
			quit(failures)
	return false

const RED_HOME := Vector2(2.75, 17.875) # the middle of Red's four unit spawns
const BLUE_HQ := Vector3(-10, 1.5, -22)
const RALLY := Vector3(-4, 0, -14)
const ORANGE := Color(1, 0.6, 0.15)
var blue_view: Vector2

func check(name: String, actual, expected):
	var ok: bool = actual == expected
	if not ok:
		failures += 1
	print("%s  %s (got %s, want %s)" % ["PASS" if ok else "FAIL", name, actual, expected])

# Which of these units show their selection ring.
func rings(views: Array) -> Array:
	return views.map(func(v): return v.get_node("SelectionRing").visible)

# A unit's health bar shows once it's hurt (HealthBar.cs, created in code as a child of the view).
func hurt(v: Node) -> bool:
	for c in v.get_children():
		var script = c.get_script()
		if script != null and str(script.resource_path).ends_with("HealthBar.cs") and c.visible:
			return true
	return false

# The colors of the order path lines UnitsView draws this frame (its path surface uses vertex colors;
# tracers don't).
func path_colors() -> Array:
	var mesh: ImmediateMesh = root.get_node("Main/UnitsView").get_child(0).mesh
	var colors := []
	for i in mesh.get_surface_count():
		var material = mesh.surface_get_material(i)
		if material is StandardMaterial3D and material.vertex_color_use_as_albedo:
			for c in mesh.surface_get_arrays(i)[Mesh.ARRAY_COLOR]:
				if not colors.any(func(k): return k.is_equal_approx(c)):
					colors.append(c)
	return colors

func selected(views: Array) -> int:
	return views.filter(func(v): return is_instance_valid(v) and v.get_node("SelectionRing").visible).size()

func screen(v: Node3D) -> Vector2:
	return cam.unproject_position(v.global_position + Vector3(0, 0.6, 0))

# BeltView draws a 0.35 x 0.15 x 1 box arrow on each of a switch's outputs (the map's one switch: north,
# then south), showing where the stream goes. Which of them show.
func arrows_shown() -> Array:
	return root.get_node("Main/BeltView").get_children() 		.filter(func(n): return n is MeshInstance3D and n.mesh is BoxMesh and n.mesh.size.is_equal_approx(Vector3(0.35, 0.15, 1))) 		.map(func(n): return n.visible)

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

func double_click(pos: Vector2):
	click(pos)
	var e := InputEventMouseButton.new()
	e.button_index = MOUSE_BUTTON_LEFT
	e.position = pos
	e.global_position = pos
	e.pressed = true
	e.double_click = true
	Input.parse_input_event(e)
	mouse(MOUSE_BUTTON_LEFT, pos, false)

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
