# Input smoke test: feeds real mouse/keyboard events through Godot's Input into the main scene and
# checks selection (only your own units), group moves, breaking an enemy belt segment (Ctrl+right-click)
# and the repair cursor over it, the hotseat swap with its per-side camera,
# double-click select by type, the cursor, attack-move, order paths (only for the selection, colored by
# order), camera pan and zoom, production (select the HQ, queue by hotkey, rally point, cancel),
# construction (a builder's build key arms a ghost, a click lays the foundation), and the minimap.
# Needs a window (headless Godot drops input events). From the repo root:
#   Godot_v4.7.2-stable_mono_win64_console.exe --path godot --fixed-fps 60 -s ../tools/input_smoke_test.gd
# Prints PASS/FAIL per check and exits with the number of failures.
extends SceneTree

const RED_BELT := Vector3(9, 2.6, 11) # on Red's belt, on the prototype map

var frame := 0
var failures := 0
var cam: Camera3D
var player: Node
var blue: Array
var red: Array
var cam_before: Vector3

func _initialize():
	change_scene_to_file("res://scenes/prototype.tscn")

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
			key(KEY_CTRL, true)
			right_click(cam.unproject_position(RED_BELT))
		590:
			key(KEY_CTRL, false)
		1120:
			# Walked into range (~2 s) and shot the 100-health segment down.
			check("Ctrl+right-click on an enemy belt segment breaks it", root.get_node("Main/SimHost").get("BrokenSegments"), 1)
			motion(cam.unproject_position(RED_BELT))
		1125:
			check("cursor over a broken segment, nobody selected who repairs: move", player.get("CursorName"), "Move")
		1130:
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
			motion(cam.unproject_position(Vector3(0, 0, -5)))
		1255:
			check("cursor over open ground: move", player.get("CursorName"), "Move")
			key(KEY_CTRL, true)
			motion(cam.unproject_position(Vector3(0, 2.6, -11))) # Blue's own belt
		1260:
			check("Ctrl over a belt: attack it", player.get("CursorName"), "Attack")
			key(KEY_CTRL, false)
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
			var panel = root.get_node("Main/Ui/CommandCard")
			check("clicking your HQ selects it and shows production", [player.get("SelectedBuilding") != -1, panel.visible], [true, true])
			key(KEY_Q, true)
			key(KEY_Q, false)
		1620:
			check("Q queues the first unit type", root.get_node("Main/Ui/CommandCard").get("QueueLength"), 1)
			right_click(cam.unproject_position(RALLY))
		1625:
			var flag: Vector3 = root.get_node("Main/BuildingsView/Rally").global_position
			check("right-click with the HQ selected sets its rally point", flag.distance_to(RALLY) < 0.3, true)
			key(KEY_BACKSPACE, true)
			key(KEY_BACKSPACE, false)
		1630:
			check("Backspace cancels the last queued unit", root.get_node("Main/Ui/CommandCard").get("QueueLength"), 0)
			click(cam.unproject_position(Vector3(-20, 0, -8))) # empty ground
		1635:
			check("clicking away deselects the HQ and hides production", [player.get("SelectedBuilding"), root.get_node("Main/Ui/CommandCard").visible], [-1, false])
			click(cam.unproject_position(BLUE_HQ))
		1640:
			key(KEY_Q, true) # a builder: the HQ's first slot
			key(KEY_Q, false)
		2400:
			# 10 s to train, then it drives to the rally point.
			builder = new_blue_unit()
			check("the HQ trained a builder", builder != null, true)
			click(screen(builder))
		2401:
			cam.set("Focus", Vector2(0, 0)) # Red's broken belt in view
		2404:
			motion(cam.unproject_position(RED_BELT))
		2409:
			check("a builder over a broken segment: repair", player.get("CursorName"), "Repair")
			cam.set("Focus", Vector2(-10, -17)) # back over Blue's HQ
		2415:
			check("selecting a builder shows its build card", root.get_node("Main/Ui/CommandCard").visible, true)
			key(KEY_Q, true) # the first thing it builds: a barracks
			key(KEY_Q, false)
			motion(cam.unproject_position(SITE))
		2420:
			check("its build key arms a ghost under the cursor", ghost().visible, true)
			check("the ghost snaps to the grid over the site", (ghost().global_position * Vector3(1, 0, 1)).distance_to(SITE) < 0.5, true)
			check("the construction grid shows while placing", root.get_node("Main/BuildingsView/Grid").visible, true)
			click(cam.unproject_position(SITE))
		2425:
			check("clicking places it and disarms the ghost", ghost().visible, false)
			check("and hides the grid", root.get_node("Main/BuildingsView/Grid").visible, false)
		3200:
			# ~16 m from the rally point, at a tracked builder's pace.
			check("the builder laid a foundation", building_count(), 3)
			var map := root.get_node("Main/Ui/Minimap") as Control
			var bounds: Rect2 = cam.get("Bounds")
			var target := Vector2(20, 10)
			click(map.global_position + (target - bounds.position) / bounds.size * map.size)
		3205:
			check("clicking the minimap moves the camera there", (cam.get("Focus") as Vector2).distance_to(Vector2(20, 10)) < 1, true)
			key(KEY_ESCAPE, true) # nothing armed: Esc pauses
			key(KEY_ESCAPE, false)
		3210:
			check("Esc with nothing to cancel opens the pause menu", root.get_node("Main/Ui/PauseMenu").visible, true)
			paused_at = root.get_node("Main/SimHost").get("ShownTick")
		3230:
			check("the game holds while paused", root.get_node("Main/SimHost").get("ShownTick"), paused_at)
			key(KEY_ESCAPE, true)
			key(KEY_ESCAPE, false)
		3235:
			check("Esc again resumes", [root.get_node("Main/Ui/PauseMenu").visible, root.get_node("Main/SimHost").get("Paused")], [false, false])
			print("DONE: %d failure(s)" % failures)
			quit(failures)
	return false

const RED_HOME := Vector2(2.75, 17.875) # the middle of Red's four unit spawns
const SITE := Vector3(-20, 0, -18)       # clear ground beside Blue's HQ
var builder: Node3D
const BLUE_HQ := Vector3(-10, 1.5, -22)
const RALLY := Vector3(-4, 0, -14)
const ORANGE := Color(1, 0.6, 0.15)
var blue_view: Vector2
var paused_at := 0

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

# A Blue unit view that wasn't there at the start (produced since).
func new_blue_unit() -> Node3D:
	for v in root.get_node("Main/UnitsView").get_children():
		if v.has_node("SelectionRing") and v.get("PlayerIndex") == 0 and not blue.has(v):
			return v
	return null

func ghost() -> Node3D:
	return root.get_node("Main/BuildingsView/Ghost")

# Buildings and foundations drawn: BuildingsView's children besides its rally flag and ghost.
func building_count() -> int:
	return root.get_node("Main/BuildingsView").get_child_count() - 3 # less the rally flag, the ghost and the grid

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

func box(views: Array):
	var lo := Vector2(INF, INF)
	var hi := Vector2(-INF, -INF)
	for v in views:
		lo = lo.min(screen(v))
		hi = hi.max(screen(v))
	mouse(MOUSE_BUTTON_LEFT, lo - Vector2(40, 40), true)
	motion(hi + Vector2(40, 40))
	mouse(MOUSE_BUTTON_LEFT, hi + Vector2(40, 40), false)

# Positions here are in the viewport's 2D space (what unproject_position gives). Injected events, like the
# OS's, are in window pixels, which the canvas_items stretch scales down again: scale them up first.
func to_window(pos: Vector2) -> Vector2:
	return pos * Vector2(DisplayServer.window_get_size()) / root.get_visible_rect().size

func mouse(button: int, pos: Vector2, pressed: bool):
	var e := InputEventMouseButton.new()
	e.button_index = button
	e.position = to_window(pos)
	e.global_position = to_window(pos)
	e.pressed = pressed
	Input.parse_input_event(e)

func motion(pos: Vector2):
	var m := InputEventMouseMotion.new()
	m.position = to_window(pos)
	m.global_position = to_window(pos)
	Input.parse_input_event(m)

func double_click(pos: Vector2):
	click(pos)
	var e := InputEventMouseButton.new()
	e.button_index = MOUSE_BUTTON_LEFT
	e.position = to_window(pos)
	e.global_position = to_window(pos)
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
