"""Regenerates the map content of the playable map, godot/scenes/main.tscn, from the layout below.

The map is point-symmetric: everything is authored for Blue (the -z half) and mirrored through the
center, (x, z) -> (-x, -z), for Red, so both sides get exactly the same map. Design rules for it are
in docs/doctrine.md (Playable map). There are no gatherer posts: players build them.

It rewrites only the map: the belt curves, and the Belts, Junctions, Gatherers (left empty), Units and
Buildings nodes, plus the ground size and the camera bounds. Everything else in the scene (views, UI,
host settings) is kept, but hand edits to the map nodes are overwritten: change the layout here instead.

From the repo root:  python tools/make_main_map.py
"""
import io
import re

SCENE = 'godot/scenes/main.tscn'
SIZE = (200, 140)  # m, x by z, centered on the origin
BELT_HEIGHT = 0.2


def neg(p):
    return (-p[0], -p[1])


# Belt lines, Blue's half: name -> [(point, in handle, out handle)], ground (x, z), handles relative to
# the point (Godot's Curve3D). A line's start at a switch is one of its outputs.
BLUE_BELTS = {
    # Enters from the west edge along Blue's home stretch (where Blue's first post goes), then up to the west switch.
    'WestLine': [((-98, -30), (0, 0), (20, 0)), ((-55, -30), (-12, 0), (12, 0)), ((-28, 0), (-5, -10), (0, 0))],
    # The west switch's two outputs: toward Blue's side of the middle, and toward Red's.
    'WestSouth': [((-28, 0), (0, 0), (8, -6)), ((-8, -38), (0, 10), (0, 0))],
    'WestNorth': [((-28, 0), (0, 0), (8, 6)), ((-8, 38), (0, -10), (0, 0))],
}
MIRRORED_NAMES = {'WestLine': 'EastLine', 'WestSouth': 'EastNorth', 'WestNorth': 'EastSouth'}
BELTS = dict(BLUE_BELTS)
for name, points in BLUE_BELTS.items():
    BELTS[MIRRORED_NAMES[name]] = [(neg(p), neg(i), neg(o)) for p, i, o in points]

SWITCHES = {'WestSwitch': (-28, 0), 'EastSwitch': (28, 0)}

BLUE_HQ = (-60, -55)  # its exit faces the center (+z)
BLUE_UNITS = [('BlueBuilder', 'builder', (-60, -46))]  # facing +z; everything else gets built


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

    s += '[node name="Gatherers" type="Node3D" parent="."]\n\n'  # none: players build their posts

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
    print('Wrote %s: %d belt lines, %d switches, %d units a side.' % (SCENE, len(BELTS), len(SWITCHES), len(BLUE_UNITS)))


if __name__ == '__main__':
    main()
