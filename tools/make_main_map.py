"""Regenerates the map content of the playable map, godot/scenes/main.tscn, from the layout below, and
checks it against the map rules in docs/doctrine.md (Playable map). Fails without writing if it doesn't pass.

The map is point-symmetric: everything is authored for Blue and mirrored through the center,
(x, z) -> (-x, -z), for Red, so both sides get exactly the same map. There are no gatherer posts:
players build them.

Layout: the HQs sit in opposite corners (Blue south-west, Red north-east). Each side's belt starts
beside its base (its home stretch), runs to the middle of the map, and turns there to follow the line
that's equally far from both HQs out to a flank edge, where it ends. The two belts meet point to point
in the middle, so that's where the fight is, and their tails run out to opposite flanks.

It rewrites only the map: the belt curves, and the Belts, Gatherers (left empty), Units and Buildings
nodes, plus the ground size and the camera bounds. Everything else in the scene (views, UI, host
settings) is kept, but hand edits to the map nodes are overwritten: change the layout here instead.

From the repo root:  python tools/make_main_map.py            (checks, then writes)
                     python tools/make_main_map.py --check    (checks and prints the report only)
"""
import io
import math
import re
import sys

SCENE = 'godot/scenes/main.tscn'
SIZE = (256, 180)  # m, x by z, centered on the origin
BELT_HEIGHT = 0.2

# Must match the sim (src/Sim/Simulation.cs: PostSpacing) and the posts' reach of the belt.
POST_SPACING = 30.0  # m along a line between any two posts
SLOT_STEP = 5.0      # m between the belt points the report samples

# The rule checks. "Lead" is how much closer a point is to one HQ than to the other, in m.
SAFE_LEAD = 50.0      # a point leading this much toward its owner's HQ is safe for the owner
CONTESTED_LEAD = 30.0 # the tail (past the second slot) leads less than this either way
BUILD_CLEARANCE = 22.0  # m from each HQ's center that no belt comes closer than

# Positions are authored in (s, t): s along the center line (the line equally far from both HQs; +s
# runs toward the south-east), t across it, toward Blue. The HQs sit on the t axis.
AXIS = (-0.832, -0.555)  # t, as a ground direction (x, z): from the center toward Blue's corner
ALONG = (-AXIS[1], AXIS[0])  # s: the center line, toward the south-east


def st(s, t):
    return (s * ALONG[0] + t * AXIS[0], s * ALONG[1] + t * AXIS[1])


BLUE_HQ = st(0, 60)  # its exit faces the middle
BLUE_BUILDER = st(6, 50)

# Blue's belt as waypoints; the curve passes through them, smoothly. Its home stretch runs parallel to
# the center line 28 m off it, 32 m in front of the HQ; then it jogs in to 4 m off the line and follows
# it to the south edge. Red's is the same, mirrored.
BLUE_BELT = [st(-24, 28), st(-6, 28), st(3, 14), st(9, 4), st(30, 4), st(105, 4)]


def neg(p):
    return (-p[0], -p[1])


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1])


def add(a, b):
    return (a[0] + b[0], a[1] + b[1])


def scale(a, k):
    return (a[0] * k, a[1] * k)


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def handles(waypoints):
    """Catmull-Rom style tangents turned into Bezier handles: [(point, in handle, out handle)], the handles
    relative to the point, as Godot's Curve3D stores them. The ends get no handle on their open side."""
    out = []
    n = len(waypoints)
    for i, p in enumerate(waypoints):
        prev = waypoints[i - 1] if i > 0 else p
        nxt = waypoints[i + 1] if i < n - 1 else p
        tangent = scale(sub(nxt, prev), 1 / 6)  # a third of the half-span either side
        out.append((p, scale(tangent, -1) if i > 0 else (0, 0), tangent if i < n - 1 else (0, 0)))
    return out


def sample(points, step):
    """Points along the curve about `step` apart, with the distance along it: [(distance, (x, z))]."""
    fine = []
    for (p0, _, o0), (p1, i1, _) in zip(points, points[1:]):
        c0, c1 = add(p0, o0), add(p1, i1)
        for k in range(200):
            t = k / 200
            u = 1 - t
            fine.append(add(add(scale(p0, u * u * u), scale(c0, 3 * u * u * t)), add(scale(c1, 3 * u * t * t), scale(p1, t * t * t))))
    fine.append(points[-1][0])
    along, out, next_at = 0.0, [], 0.0
    for a, b in zip(fine, fine[1:] + [fine[-1]]):
        if along >= next_at:
            out.append((along, a))
            next_at += step
        along += dist(a, b)
    return out, along


BELTS = {'BlueBelt': handles(BLUE_BELT), 'RedBelt': handles([neg(p) for p in BLUE_BELT])}
HQS = {'BlueBelt': (BLUE_HQ, neg(BLUE_HQ)), 'RedBelt': (neg(BLUE_HQ), BLUE_HQ)}


def check():
    """Prints the balance report and returns the list of rule failures."""
    failures = []
    half = (SIZE[0] / 2, SIZE[1] / 2)
    for name, points in BELTS.items():
        own, enemy = HQS[name]
        samples, length = sample(points, SLOT_STEP)
        print('%s: %.0f m long; lead toward its owner\'s HQ (m) every %g m along it:' % (name, length, SLOT_STEP))
        leads = [(d, dist(p, enemy) - dist(p, own), p) for d, p in samples]
        for d, lead, p in leads:
            mark = 'safe' if lead >= SAFE_LEAD else ('contested' if abs(lead) < CONTESTED_LEAD else 'leaning')
            print('  %5.0f m  (%6.1f, %6.1f)  lead %6.1f  %s' % (d, p[0], p[1], lead, mark))

        safe = [d for d, lead, _ in leads if lead >= SAFE_LEAD]
        if not safe or safe[0] != 0:
            failures.append('%s: its start is not safe for its owner' % name)
        elif max(safe) - min(safe) >= POST_SPACING:
            failures.append('%s: the safe stretch is %.0f m long, room for two posts (spacing %g m)' % (name, max(safe) - min(safe), POST_SPACING))
        # Past the second slot (spacing after the safe stretch ends), everything is contested.
        tail_from = (max(safe) if safe else 0) + POST_SPACING
        leaning = [(d, lead) for d, lead, _ in leads if d >= tail_from and abs(lead) >= CONTESTED_LEAD]
        if leaning:
            failures.append('%s: past %.0f m it still leans %.0f m at %.0f m' % (name, tail_from, leaning[0][1], leaning[0][0]))
        for d, _, p in leads:
            for hq in (own, enemy):
                if dist(p, hq) < BUILD_CLEARANCE:
                    failures.append('%s: at %.0f m it passes %.1f m from an HQ (keep %g m clear)' % (name, d, dist(p, hq), BUILD_CLEARANCE))
            if abs(p[0]) > half[0] + 0.01 or abs(p[1]) > half[1] + 0.01:
                failures.append('%s: at %.0f m it leaves the map' % (name, d))
        end = points[-1][0]
        end_lead = dist(end, enemy) - dist(end, own)
        if abs(end_lead) >= CONTESTED_LEAD:
            failures.append('%s: its end leads %.0f m, not on a neutral flank' % (name, end_lead))
    # The belts never touch (no junctions, no crossings).
    a, _ = sample(BELTS['BlueBelt'], 1.0)
    b, _ = sample(BELTS['RedBelt'], 1.0)
    closest = min(dist(p, q) for _, p in a for _, q in b)
    print('Closest the two belts come: %.1f m' % closest)
    if closest < 6:
        failures.append('the belts come within %.1f m of each other' % closest)
    return failures


def num(x):
    return '%g' % round(x, 3)


def transform(x, z, y=0.0, facing=None):
    # A marker's -Z is its facing; `facing` is a ground direction (x, z) to turn it toward.
    if facing is None:
        basis = '1, 0, 0, 0, 1, 0, 0, 0, 1'
    else:
        fx, fz = facing
        k = math.hypot(fx, fz)
        fx, fz = fx / k, fz / k
        # Columns: X = (-fz, 0, fx)... Godot's Basis in a Transform3D is written row-major by columns x, y, z.
        # -Z points along the facing, so Z = (-fx, 0, -fz); X = Y cross Z = (-fz, 0, fx).
        basis = '%s, 0, %s, 0, 1, 0, %s, 0, %s' % (num(-fz), num(-fx), num(fx), num(-fz))
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

    s += '[node name="Gatherers" type="Node3D" parent="."]\n\n'  # none: players build their posts

    toward_middle = neg(BLUE_HQ)
    s += '[node name="Units" type="Node3D" parent="."]\n\n'
    s += '[node name="BlueBuilder" type="Marker3D" parent="Units"]\n%s\nscript = ExtResource("8_spawn")\nUnitType = "builder"\n\n' % (
        transform(*BLUE_BUILDER, facing=toward_middle))
    s += '[node name="RedBuilder" type="Marker3D" parent="Units"]\n%s\nscript = ExtResource("8_spawn")\nPlayer = 1\nUnitType = "builder"\n\n' % (
        transform(*neg(BLUE_BUILDER), facing=neg(toward_middle)))

    s += '[node name="Buildings" type="Node3D" parent="."]\n\n'
    s += '[node name="BlueHQ" type="Marker3D" parent="Buildings"]\n%s\nscript = ExtResource("10_bspawn")\n\n' % transform(*BLUE_HQ, facing=toward_middle)
    s += '[node name="RedHQ" type="Marker3D" parent="Buildings"]\n%s\nscript = ExtResource("10_bspawn")\nPlayer = 1\n\n' % transform(*neg(BLUE_HQ), facing=neg(toward_middle))
    return s


def main():
    failures = check()
    if failures:
        print('\nFAILED the map rules:')
        for f in failures:
            print('  ' + f)
        sys.exit(1)
    print('\nPasses the map rules.')
    if '--check' in sys.argv:
        return
    scene = io.open(SCENE, encoding='utf-8').read()
    scene = re.sub(r'\[sub_resource type="Curve3D".*?point_count = \d+\n\n', '', scene, flags=re.S)
    root = '[node name="Main" type="Node3D"]'
    scene = scene.replace(root, curves() + root)
    start, end = scene.index('[node name="Belts"'), scene.index('[node name="BeltView"')
    scene = scene[:start] + map_nodes() + scene[end:]
    scene = re.sub(r'(id="PlaneMesh_ground"\]\n(?:.*\n)*?)size = Vector2\([^)]*\)', r'\g<1>size = Vector2(%s, %s)' % SIZE, scene, count=1)
    scene = re.sub(r'Bounds = Rect2\([^)]*\)', 'Bounds = Rect2(%s, %s, %s, %s)' % (-SIZE[0] // 2, -SIZE[1] // 2, SIZE[0], SIZE[1]), scene, count=1)
    io.open(SCENE, 'w', encoding='utf-8', newline='\n').write(scene)
    print('Wrote %s: %d belts.' % (SCENE, len(BELTS)))


if __name__ == '__main__':
    main()
