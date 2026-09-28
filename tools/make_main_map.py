"""Regenerates the map content of the playable map, godot/scenes/main.tscn, from the layout below.

The map is point-symmetric: everything is authored for Blue (the -z half) and mirrored through the
center, (x, z) -> (-x, -z), for Red, so both sides get exactly the same map. Design rules for it are
in docs/doctrine.md (Playable map).

It rewrites only the map: the belt curves, and the Belts, Junctions, Gatherers, Units and Buildings
nodes, plus the ground size and the camera bounds. Everything else in the scene (views, UI, host
settings) is kept, but hand edits to the map nodes are overwritten: change the layout here instead.

From the repo root:  python tools/make_main_map.py
"""
import io
import math
import re

SCENE = 'godot/scenes/main.tscn'
SIZE = (200, 140)  # m, x by z, centered on the origin
BELT_HEIGHT = 0.2
POST_OFFSET = 3.0  # m from the belt center; SimHost attaches a post to a belt within GathererReach (4 m)


def neg(p):
    return (-p[0], -p[1])


# Belt lines, Blue's half: name -> [(point, in handle, out handle)], ground (x, z), handles relative to
# the point (Godot's Curve3D). A line's start at a switch is one of its outputs.
BLUE_BELTS = {
    # Enters from the west edge, runs past Blue's home post, then up to the west switch.
    'WestLine': [((-98, -30), (0, 0), (20, 0)), ((-55, -30), (-12, 0), (12, 0)), ((-28, 0), (-5, -10), (0, 0))],
    # The west switch's two outputs: toward Blue's forward posts, and toward Red's.
    'WestSouth': [((-28, 0), (0, 0), (8, -6)), ((-8, -38), (0, 10), (0, 0))],
    'WestNorth': [((-28, 0), (0, 0), (8, 6)), ((-8, 38), (0, -10), (0, 0))],
}
MIRRORED_NAMES = {'WestLine': 'EastLine', 'WestSouth': 'EastNorth', 'WestNorth': 'EastSouth'}
BELTS = dict(BLUE_BELTS)
for name, points in BLUE_BELTS.items():
    BELTS[MIRRORED_NAMES[name]] = [(neg(p), neg(i), neg(o)) for p, i, o in points]

SWITCHES = {'WestSwitch': (-28, 0), 'EastSwitch': (28, 0)}

# Blue's posts, as (belt, segment, t along it, side): side -1 or +1 is left or right of the flow.
BLUE_POSTS = {
    'BlueHomePost': ('WestLine', 0, 0.45, -1),    # on its own home stretch, deep in Blue's half
    'BlueForwardWest': ('WestSouth', 0, 0.7, +1),  # near the ends of both branches toward Blue
    'BlueForwardEast': ('EastSouth', 0, 0.7, -1),
}
RED_POST_NAMES = {'BlueHomePost': 'RedHomePost', 'BlueForwardWest': 'RedForwardEast', 'BlueForwardEast': 'RedForwardWest'}

BLUE_HQ = (-60, -55)  # its exit faces the center (+z)
BLUE_UNITS = [('BlueSquad1', 'rifle_squad', (-54, -42)), ('BlueSquad2', 'rifle_squad', (-48, -42)),
              ('BlueTank', 'tank', (-54, -47)), ('BlueCar', 'scout_car', (-48, -47))]  # facing +z


def bezier(points, segment, t):
    """Position and unit direction on a belt at parameter t of one of its cubic segments."""
    (a, _, a_out), (b, b_in, _) = points[segment], points[segment + 1]
    c1, c2 = (a[0] + a_out[0], a[1] + a_out[1]), (b[0] + b_in[0], b[1] + b_in[1])
    u = 1 - t
    pos = [u ** 3 * a[k] + 3 * u * u * t * c1[k] + 3 * u * t * t * c2[k] + t ** 3 * b[k] for k in range(2)]
    d = [3 * u * u * (c1[k] - a[k]) + 6 * u * t * (c2[k] - c1[k]) + 3 * t * t * (b[k] - c2[k]) for k in range(2)]
    n = math.hypot(*d)
    return pos, (d[0] / n, d[1] / n)


def beside(belt, segment, t, side):
    pos, d = bezier(BELTS[belt], segment, t)
    right = (-d[1], d[0])
    return (round(pos[0] + right[0] * POST_OFFSET * side, 2), round(pos[1] + right[1] * POST_OFFSET * side, 2))


def num(x):
    return '%g' % x


def transform(x, z, y=0.0, facing_positive_z=False):
    # A marker's -Z is its facing; turned 180° it faces +z.
    basis = '-1, 0, 0, 0, 1, 0, 0, 0, -1' if facing_positive_z else '1, 0, 0, 0, 1, 0, 0, 0, 1'
    return 'transform = Transform3D(%s, %s, %s, %s)' % (basis, num(x), num(y), num(z))


def curves():
    out = []
    for name, points in BELTS.items():
        flat = []
        for p, i, o in points:
            flat += [i[0], 0, i[1], o[0], 0, o[1], p[0], BELT_HEIGHT, p[1]]
        out.append('[sub_resource type="Curve3D" id="Curve3D_%s"]\n_data = {\n"points": PackedVector3Array(%s),\n'
                   '"tilts": PackedFloat32Array(%s)\n}\npoint_count = %d\n\n'
                   % (name, ', '.join(num(x) for x in flat), ', '.join('0' for _ in points), len(points)))
    return ''.join(out)


def map_nodes():
    s = '[node name="Belts" type="Node3D" parent="."]\n\n'
    for name in BELTS:
        s += '[node name="%s" type="Path3D" parent="Belts"]\ncurve = SubResource("Curve3D_%s")\n\n' % (name, name)

    s += '[node name="Junctions" type="Node3D" parent="."]\n\n'
    for name, (x, z) in SWITCHES.items():
        s += '[node name="%s" type="Marker3D" parent="Junctions"]\n%s\n\n' % (name, transform(x, z, BELT_HEIGHT))

    s += '[node name="Gatherers" type="Node3D" parent="."]\n\n'
    posts = {}
    for name, spec in BLUE_POSTS.items():
        posts[name] = (0, beside(*spec))
        posts[RED_POST_NAMES[name]] = (1, neg(posts[name][1]))
    for name, (player, (x, z)) in posts.items():
        s += '[node name="%s" type="Marker3D" parent="Gatherers"]\n%s\nscript = ExtResource("7_owned")\n%s\n' % (
            name, transform(x, z), 'Player = 1\n' if player else '')

    s += '[node name="Units" type="Node3D" parent="."]\n\n'
    for name, kind, (x, z) in BLUE_UNITS:
        s += '[node name="%s" type="Marker3D" parent="Units"]\n%s\nscript = ExtResource("8_spawn")\nUnitType = "%s"\n\n' % (
            name, transform(x, z, facing_positive_z=True), kind)
    for name, kind, (x, z) in BLUE_UNITS:
        s += '[node name="%s" type="Marker3D" parent="Units"]\n%s\nscript = ExtResource("8_spawn")\nPlayer = 1\nUnitType = "%s"\n\n' % (
            name.replace('Blue', 'Red'), transform(-x, -z), kind)

    s += '[node name="Buildings" type="Node3D" parent="."]\n\n'
    s += '[node name="BlueHQ" type="Marker3D" parent="Buildings"]\n%s\nscript = ExtResource("10_bspawn")\n\n' % transform(*BLUE_HQ, facing_positive_z=True)
    s += '[node name="RedHQ" type="Marker3D" parent="Buildings"]\n%s\nscript = ExtResource("10_bspawn")\nPlayer = 1\n\n' % transform(*neg(BLUE_HQ))
    return s


def main():
    scene = io.open(SCENE, encoding='utf-8').read()
    scene = re.sub(r'\[sub_resource type="Curve3D".*?point_count = \d+\n\n', '', scene, flags=re.S)
    root = '[node name="Main" type="Node3D"]'
    scene = scene.replace(root, curves() + root)
    start, end = scene.index('[node name="Belts"'), scene.index('[node name="BeltView"')
    scene = scene[:start] + map_nodes() + scene[end:]
    scene = re.sub(r'(id="PlaneMesh_ground"\]\n(?:.*\n)*?)size = Vector2\([^)]*\)', r'\g<1>size = Vector2(%s, %s)' % SIZE, scene, count=1)
    scene = re.sub(r'Bounds = Rect2\([^)]*\)', 'Bounds = Rect2(%s, %s, %s, %s)' % (-SIZE[0] // 2, -SIZE[1] // 2, SIZE[0], SIZE[1]), scene, count=1)
    io.open(SCENE, 'w', encoding='utf-8', newline='\n').write(scene)
    print('Wrote %s: %d belt lines, %d switches, %d posts.' % (SCENE, len(BELTS), len(SWITCHES), 2 * len(BLUE_POSTS)))


if __name__ == '__main__':
    main()
