"""Regenerates the map content of the playable map, godot/scenes/main.tscn, from the layout below, and
checks it against the map rules in docs/doctrine.md (Playable map). Fails without writing if it doesn't pass.

The map is point-symmetric: everything is authored for Blue and mirrored through the center,
(x, z) -> (-x, -z), for Red, so both sides get exactly the same map. There are no gatherer posts:
players build them.

Layout: the HQs sit in opposite corners (Blue south-west, Red north-east). Each side's belt comes in
from beyond the map edge, covered (unbreakable, no posts) through its owner's back field, and opens in
front of its base (its home stretch). It runs to the middle of the map and turns there to follow the
line that's equally far from both HQs out to a flank, and leaves the map again, covered for its last
stretch. The two belts meet in the middle, so that's where the fight is, and their tails run out to
opposite flanks. The rule checks count open belt only.

Rocks (obstacles: solid, nothing crosses or builds on them) stand in the open ground: a ring of four
round the middle, where the belts cross, leaving lanes between them, and one out on each flank beside a
belt's tail. They keep clear of the belts (so posts, spills and repairs are never in them) and of the
bases (room to build).

It rewrites only the map: the belt curves, and the Belts, Gatherers (left empty), Units, Buildings and
Obstacles nodes, plus the ground size and the camera bounds (the camera's bounds are the map, for the sim's
navigation too). Everything else in the scene (views, UI, host
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
BELT_HEIGHT = 0  # m: roads lie on the ground (the belts they replaced stood on 2.6 m legs)

# Must match the sim (src/Sim/Simulation.cs: PostSpacing) and the posts' reach of the belt.
POST_SPACING = 30.0  # m along a line between any two posts
SLOT_STEP = 5.0      # m between the belt points the report samples

# The rule checks. "Lead" is how much closer a point is to one HQ than to the other, in m.
SAFE_LEAD = 50.0      # a point leading this much toward its owner's HQ is safe for the owner
CONTESTED_LEAD = 30.0 # the tail (past the second slot) leads less than this either way
BUILD_CLEARANCE = 22.0  # m from each HQ's center that no belt comes closer than
ROCK_BELT_CLEARANCE = 8.0  # m between a rock and any belt
ROCK_HQ_CLEARANCE = 40.0   # m from each HQ's center to any rock: the AI builds up to this far out
ROCK_GAP = 10.0            # m at least between two rocks, so every lane takes a tank with room to spare
HOUSE_BELT_CLEARANCE = 8.0  # m from a house's center to any belt: room to walk round it
HOUSE_REACH = 12.0          # m from a house's center that a rifle squad inside reaches (8 m, +2 m, +half its 4 m): both roads within it
HOUSE_ROCK_CLEARANCE = 8.0  # m from a house's center to any rock

# Positions are authored in (s, t): s along the center line (the line equally far from both HQs; +s
# runs toward the south-east), t across it, toward Blue. The HQs sit on the t axis.
AXIS = (-0.832, -0.555)  # t, as a ground direction (x, z): from the center toward Blue's corner
ALONG = (-AXIS[1], AXIS[0])  # s: the center line, toward the south-east


def st(s, t):
    return (s * ALONG[0] + t * AXIS[0], s * ALONG[1] + t * AXIS[1])


BLUE_HQ = st(0, 60)  # its exit faces the middle
BLUE_BUILDER = st(6, 50)

# Blue's belt as waypoints; the curve passes through them, smoothly. It comes in covered from beyond the
# west edge (ENTRY), opens where its home stretch starts, runs parallel to the center line 28 m off it,
# 32 m in front of the HQ, then jogs in to 4 m off the line and follows it to the south edge, and is
# covered again from the last open waypoint out past the edge (EXIT). Red's is the same, mirrored.
BLUE_ENTRY = [(-140, 20), (-100, 18), (-62, 16)]
BLUE_OPEN = [st(-24, 28), st(-6, 28), st(3, 14), st(9, 4), st(30, 4), st(100, 4)]
BLUE_EXIT = [st(125, 4)]
BLUE_BELT = BLUE_ENTRY + BLUE_OPEN + BLUE_EXIT


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

# Blue's rocks, each (s, t, length, thickness, turn): the middle in (s, t), its length (m) along the center
# line turned by `turn` degrees, and its thickness across that. Red's are the same, mirrored.
BLUE_ROCKS = [
    (-17, 15, 10, 4, 20),   # the ring round the middle: north-west of the crossing
    (20, 15, 10, 4, -15),   # and north-east of it, beside Blue's tail
    (70, 16, 8, 5, 10),     # out on the flank, beside Blue's tail
]
ROCK_HEIGHT = 2.2

# Blue's garrison houses (neutral buildings infantry go into), each (s, t): at the crossing, on Blue's side
# of the center line, where a squad inside reaches both roads. Red's are the same, mirrored. (Beside the
# tails, 2026-10-01, they were useless: nobody fought there.)
BLUE_HOUSES = [(-4.5, 5.5)]


def houses():
    out = [st(s, t) for s, t in BLUE_HOUSES]
    return out + [neg(p) for p in out]


def rocks():
    """Every rock as (center (x, z), length direction (x, z), length, thickness), Blue's then Red's."""
    out = []
    for s, t, length, thick, turn in BLUE_ROCKS:
        a = math.radians(turn)
        d = (ALONG[0] * math.cos(a) - AXIS[0] * math.sin(a), ALONG[1] * math.cos(a) - AXIS[1] * math.sin(a))
        out.append((st(s, t), d, length, thick))
    return out + [(neg(c), neg(d), length, thick) for c, d, length, thick in out]


def rock_distance(p, rock):
    """From a ground point to a rock's footprint, m (0 inside it)."""
    c, d, length, thick = rock
    rel = sub(p, c)
    u, v = rel[0] * d[0] + rel[1] * d[1], rel[0] * -d[1] + rel[1] * d[0]
    return math.hypot(max(abs(u) - length / 2, 0), max(abs(v) - thick / 2, 0))


def rock_corners(rock):
    c, d, length, thick = rock
    n = (-d[1], d[0])
    return [add(c, add(scale(d, a * length / 2), scale(n, b * thick / 2))) for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1))]


def piece_lengths(points):
    return [sample([a, b], 0.25)[1] for a, b in zip(points, points[1:])]


# How much of each end is covered: the entry pieces up to where the open belt starts, and the exit pieces.
_pieces = piece_lengths(BELTS['BlueBelt'])
COVERED = (sum(_pieces[:len(BLUE_ENTRY)]), sum(_pieces[len(_pieces) - len(BLUE_EXIT):]))
HQS = {'BlueBelt': (BLUE_HQ, neg(BLUE_HQ)), 'RedBelt': (neg(BLUE_HQ), BLUE_HQ)}

# Each route is paved road from its start through the crossing, to where its tail is PAVED_PAST m along
# the center line (s), and a dirt track along the contested tail: the rich depots out there break easily.
PAVED_PAST = 15.0


def paved_to():
    for d, p in sample(BELTS['BlueBelt'], 0.5)[0]:
        if d > COVERED[0] and p[0] * ALONG[0] + p[1] * ALONG[1] >= PAVED_PAST:
            return d
    return 0.0


PAVED_TO = paved_to()


def check():
    """Prints the balance report and returns the list of rule failures."""
    failures = []
    half = (SIZE[0] / 2, SIZE[1] / 2)
    for name, points in BELTS.items():
        own, enemy = HQS[name]
        samples, length = sample(points, SLOT_STEP)
        start, end = COVERED[0], length - COVERED[1]
        print('%s: %.0f m long, open from %.0f to %.0f m; lead toward its owner\'s HQ (m) along the open belt:' % (name, length, start, end))
        # Measured from where the open belt starts, since that's where posts go.
        leads = [(d - start, dist(p, enemy) - dist(p, own), p) for d, p in samples if start <= d <= end]
        for d, lead, p in leads:
            mark = 'safe' if lead >= SAFE_LEAD else ('contested' if abs(lead) < CONTESTED_LEAD else 'leaning')
            print('  %5.0f m  (%6.1f, %6.1f)  lead %6.1f  %s' % (d, p[0], p[1], lead, mark))
        for label, p in (('start', points[0][0]), ('end', points[-1][0])):
            if abs(p[0]) <= half[0] and abs(p[1]) <= half[1]:
                failures.append('%s: its %s is inside the map; belts come from and go beyond its edges' % (name, label))

        # The safe stretch runs from where the open belt starts (samples fall every SLOT_STEP m, so up to one
        # step past the last safe one counts too).
        safe = [d for d, lead, _ in leads if lead >= SAFE_LEAD]
        if not leads or leads[0][1] < SAFE_LEAD:
            failures.append('%s: where its open belt starts is not safe for its owner' % name)
        elif max(safe) + SLOT_STEP >= POST_SPACING:
            failures.append('%s: the safe stretch is up to %.0f m long, room for two posts (spacing %g m)' % (name, max(safe) + SLOT_STEP, POST_SPACING))
        # Past the second slot (spacing after the safe stretch ends), everything is contested.
        tail_from = (max(safe) if safe else 0) + POST_SPACING
        leaning = [(d, lead) for d, lead, _ in leads if d >= tail_from and abs(lead) >= CONTESTED_LEAD]
        if leaning:
            failures.append('%s: past %.0f m it still leans %.0f m at %.0f m' % (name, tail_from, leaning[0][1], leaning[0][0]))
        for d, p in samples:
            for hq in (own, enemy):
                if dist(p, hq) < BUILD_CLEARANCE:
                    failures.append('%s: at %.0f m it passes %.1f m from an HQ (keep %g m clear)' % (name, d, dist(p, hq), BUILD_CLEARANCE))
        for d, _, p in leads:
            if abs(p[0]) > half[0] + 0.01 or abs(p[1]) > half[1] + 0.01:
                failures.append('%s: at %.0f m the open belt leaves the map' % (name, d))
        last_open = leads[-1][2]
        end_lead = dist(last_open, enemy) - dist(last_open, own)
        if abs(end_lead) >= CONTESTED_LEAD:
            failures.append('%s: its end leads %.0f m, not on a neutral flank' % (name, end_lead))
    # Rocks keep clear of the belts, the bases, the map's edge and each other.
    all_rocks = rocks()
    belt_points = [p for points in BELTS.values() for _, p in sample(points, 1.0)[0]]
    for i, rock in enumerate(all_rocks):
        near_belt = min(rock_distance(p, rock) for p in belt_points)
        near_hq = min(rock_distance(hq, rock) for hq in (BLUE_HQ, neg(BLUE_HQ)))
        print('Rock %d at (%.1f, %.1f): %.1f m from a belt, %.1f m from an HQ' % (i, rock[0][0], rock[0][1], near_belt, near_hq))
        if near_belt < ROCK_BELT_CLEARANCE:
            failures.append('rock %d comes within %.1f m of a belt (keep %g m)' % (i, near_belt, ROCK_BELT_CLEARANCE))
        if near_hq < ROCK_HQ_CLEARANCE:
            failures.append('rock %d comes within %.1f m of an HQ (keep %g m)' % (i, near_hq, ROCK_HQ_CLEARANCE))
        if any(abs(x) > half[0] - 2 or abs(z) > half[1] - 2 for x, z in rock_corners(rock)):
            failures.append('rock %d reaches the map edge' % i)
        for j in range(i + 1, len(all_rocks)):
            gap = min(min(rock_distance(p, all_rocks[j]) for p in rock_corners(rock)), min(rock_distance(p, rock) for p in rock_corners(all_rocks[j])))
            if gap < ROCK_GAP:
                failures.append('rocks %d and %d are %.1f m apart (keep %g m)' % (i, j, gap, ROCK_GAP))

    # Houses keep clear of the belts and rocks, and stand where the fighting is: both roads within reach.
    for i, h in enumerate(houses()):
        near_belt = min(dist(h, p) for p in belt_points)
        reach = max(min(dist(h, p) for _, p in sample(points, 1.0)[0]) for points in BELTS.values())
        if reach > HOUSE_REACH:
            failures.append('house %d is %.1f m from a road: a squad inside reaches %g m' % (i, reach, HOUSE_REACH))
        near_rock = min(rock_distance(h, rock) for rock in all_rocks)
        lead = dist(h, neg(BLUE_HQ)) - dist(h, BLUE_HQ)
        print('House %d at (%.1f, %.1f): %.1f m from a belt, %.1f m from a rock, lead toward Blue %.1f' % (i, h[0], h[1], near_belt, near_rock, lead))
        if near_belt < HOUSE_BELT_CLEARANCE:
            failures.append('house %d comes within %.1f m of a belt (keep %g m)' % (i, near_belt, HOUSE_BELT_CLEARANCE))
        if near_rock < HOUSE_ROCK_CLEARANCE:
            failures.append('house %d comes within %.1f m of a rock (keep %g m)' % (i, near_rock, HOUSE_ROCK_CLEARANCE))
        if abs(lead) >= SAFE_LEAD:
            failures.append('house %d leads %.0f m toward a side; houses stand where the fighting is' % (i, lead))

    # The belts never touch (no junctions, no crossings).
    a, _ = sample(BELTS['BlueBelt'], 1.0)
    b, _ = sample(BELTS['RedBelt'], 1.0)
    closest = min(dist(p, q) for _, p in a for _, q in b)
    print('Closest the two belts come: %.1f m' % closest)
    print('Paved from the start to %.0f m (open from %.0f m), dirt beyond' % (PAVED_TO, COVERED[0]))
    if PAVED_TO <= COVERED[0]:
        failures.append('no open road is paved')
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
        s += ('[node name="%s" type="Path3D" parent="Belts"]\ncurve = SubResource("Curve3D_%s")\nscript = ExtResource("16_beltpath")\n'
              'CoveredStart = %s\nCoveredEnd = %s\nPavedTo = %s\n\n' % (name, name, num(COVERED[0]), num(COVERED[1]), num(PAVED_TO)))

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

    for i, h in enumerate(houses()):
        facing = toward_middle if i < len(BLUE_HOUSES) else neg(toward_middle)
        s += '[node name="House%d" type="Marker3D" parent="Buildings"]\n%s\nscript = ExtResource("10_bspawn")\nPlayer = -1\nBuildingType = "house"\n\n' % (
            i, transform(*h, facing=facing))

    s += '[node name="Obstacles" type="Node3D" parent="."]\n\n'
    for i, (c, d, length, thick) in enumerate(rocks()):
        # Size is (width, height, depth), its depth along the node's facing (-Z).
        s += '[node name="Rock%d" type="Node3D" parent="Obstacles"]\n%s\nscript = ExtResource("17_obstacle")\nSize = Vector3(%s, %s, %s)\n\n' % (
            i, transform(*c, facing=d), num(thick), num(ROCK_HEIGHT), num(length))
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
    for script in ('[ext_resource type="Script" path="res://scripts/BeltPath.cs" id="16_beltpath"]\n',
                   '[ext_resource type="Script" path="res://scripts/MapObstacle.cs" id="17_obstacle"]\n'):
        if script not in scene:
            first = scene.index('[ext_resource')
            scene = scene[:first] + script + scene[first:]
    # SimHost reads the rocks from the Obstacles node.
    if 'Obstacles = NodePath' not in scene:
        scene = scene.replace('node_paths=PackedStringArray("Belts", "Gatherers", "Units", "Buildings",',
                              'node_paths=PackedStringArray("Belts", "Gatherers", "Units", "Buildings", "Obstacles",', 1)
        scene = scene.replace('Buildings = NodePath("../Buildings")\n', 'Buildings = NodePath("../Buildings")\nObstacles = NodePath("../Obstacles")\n', 1)
    scene = re.sub(r'\[sub_resource type="Curve3D".*?point_count = \d+\n\n', '', scene, flags=re.S)
    root = '[node name="Main" type="Node3D"]'
    scene = scene.replace(root, curves() + root)
    start, end = scene.index('[node name="Belts"'), scene.index('[node name="BeltView"')
    scene = scene[:start] + map_nodes() + scene[end:]
    scene = re.sub(r'(id="PlaneMesh_ground"\]\n(?:.*\n)*?)size = Vector2\([^)]*\)', r'\g<1>size = Vector2(%s, %s)' % SIZE, scene, count=1)
    scene = re.sub(r'Bounds = Rect2\([^)]*\)', 'Bounds = Rect2(%s, %s, %s, %s)' % (-SIZE[0] // 2, -SIZE[1] // 2, SIZE[0], SIZE[1]), scene, count=1)
    io.open(SCENE, 'w', encoding='utf-8', newline='\n').write(scene)
    print('Wrote %s: %d belts, %d rocks, %d houses.' % (SCENE, len(BELTS), len(rocks()), len(houses())))


if __name__ == '__main__':
    main()
