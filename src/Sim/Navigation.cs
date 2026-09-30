using System.Numerics;
using System.Runtime.InteropServices;

namespace Sim;

/// <summary>
/// Getting around: paths on the NavGrid around what's solid, and units pushing each other apart. Off until
/// the host calls EnableNavigation with the map's bounds; without it units go straight at their targets
/// and pass through everything, as simple test maps want.
/// </summary>
public sealed partial class Simulation
{
    const int MaxSearchesPerTick = 12;       // path searches a tick for each player; the rest wait a tick or two
    const float RepathDistance = 1.5f;       // m a goal may move before its path is planned again
    const int SightCheckTicks = 5;           // how often units on paths look past their next waypoint: all on the same ticks, never by id (ids favour one side)
    const float FootWaypointReach = 0.3f;    // m from a waypoint that counts as there, on foot
    const float VehicleWaypointReach = 1.5f; // m, for vehicles (at least their radius)
    const float CrowdRadius = 12f;           // m from its goal within which a unit settles against others there
    const float CrowdSlack = 0.3f;           // m between two units' edges that counts as touching
    const float PushShare = 0.5f;            // of the overlap between two units taken out each tick
    const float MaxPush = 0.2f;              // m a tick a unit can be pushed (4 m/s)
    const float MovingMass = 4f;             // a unit under way is this much harder to push than one standing
    const float MaxUnstick = 0.25f;          // m a tick a unit caught in something solid is pushed out
    const float DriftBack = 1.5f;            // m an idle unit is shoved off its spot before it walks back, once free
    const float BucketSize = 4f;             // m; the push's neighbour search looks in these squares

    /// <summary>The ground's cells, once EnableNavigation has been called; null before.</summary>
    public NavGrid? Nav { get; private set; }

    bool _navDirty;
    int[] _searchesLeft = []; // this tick, by player: each has its own, so whoever's units come first in the list gets no edge
    readonly List<NavPath> _paths = [];
    readonly Stack<int> _freePaths = new();
    int _bucketsX, _bucketsZ;
    int[] _bucketHead = [], _bucketNext = [];
    Vector3[] _push = [];

    // A unit's path: waypoints, the one it's heading for, the goal and grid it was planned for.
    sealed class NavPath
    {
        public readonly List<Vector3> Points = [];
        public int Next, Version = -1;
        public Vector3 Goal;
        public bool Planned;
    }

    /// <summary>
    /// Turns on paths and pushing, over the map's ground from (minX, minZ) to (maxX, maxZ): nothing gets
    /// beyond it. Call it at setup, before or after adding things.
    /// </summary>
    public void EnableNavigation(float minX, float minZ, float maxX, float maxZ)
    {
        Nav = new NavGrid(minX, minZ, maxX, maxZ);
        _bucketsX = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / BucketSize));
        _bucketsZ = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / BucketSize));
        _bucketHead = new int[_bucketsX * _bucketsZ];
        _navDirty = true;
    }

    /// <summary>
    /// Solid ground nothing crosses (rock, a cliff): a rectangle of the given half sizes, its width along
    /// (cos h, 0, -sin h) and its depth along its heading h's (sin h, 0, cos h). Nothing can be built on it.
    /// </summary>
    public void AddObstacle(Vector3 center, float halfWidth, float halfDepth, float heading = 0)
    {
        State.Obstacles.Add(new Obstacle(center, halfWidth, halfDepth, heading));
        _navDirty = true;
    }

    /// <summary>The waypoints a unit is following and the index of the one it's heading for; null if it has no path.</summary>
    public IReadOnlyList<Vector3>? PathOf(in Unit unit, out int next)
    {
        next = 0;
        if (unit.PathSlot < 0 || unit.Current.Kind == UnitOrder.None) return null;
        var path = _paths[unit.PathSlot];
        if (!path.Planned || path.Points.Count == 0) return null;
        next = path.Next;
        return path.Points;
    }

    // A new tick's search budgets, one per player.
    void ResetSearches()
    {
        if (_searchesLeft.Length != State.Players.Count) _searchesLeft = new int[State.Players.Count];
        Array.Fill(_searchesLeft, MaxSearchesPerTick);
    }

    // What's solid changed: the grid is marked again from scratch before the next units update.
    void RebuildNav()
    {
        if (!_navDirty || Nav is null) return;
        _navDirty = false;
        Nav.Clear();
        foreach (var o in State.Obstacles) Nav.Block(o.Position, o.HalfWidth, o.HalfDepth, o.Heading);
        foreach (var b in State.Buildings) Nav.Block(b.Position, b.Type.Size / 2, b.Type.Size / 2);
        foreach (var g in State.Gatherers) Nav.Block(g.Position, PostHalfSize, PostHalfSize);
        foreach (var u in State.Units)
            if (u.Movement == Movement.Static) Nav.Block(u.Position, GridCell / 2, GridCell / 2);
        Nav.Finish();
    }

    // ---- Moving along paths ----

    // Moves the unit toward `target` this tick, by its kind of movement and, with navigation on, along a
    // path around what's solid; true on arrival (with navigation: at the nearest place to it the unit fits,
    // or settled against others already there).
    bool Move(ref Unit unit, Vector3 target)
    {
        if (unit.Movement == Movement.Static) return false;
        unit.Driving = true;
        if (Nav is null) return Step(ref unit, target, final: true);

        var path = PathFor(ref unit);
        if ((!path.Planned || path.Version != Nav.Version || GroundDistanceSq(path.Goal, target) > RepathDistance * RepathDistance)
            && !Plan(ref unit, path, target))
        {
            unit.Driving = false; // waiting its turn for a search
            return false;
        }
        if (path.Points.Count == 0)
        {
            unit.Driving = false; // boxed in: this is as far as it gets
            return true;
        }
        // On to the next waypoint once there, or once the one after it is in plain sight.
        float reach = unit.Movement == Movement.Foot ? FootWaypointReach : MathF.Max(VehicleWaypointReach, unit.Radius);
        while (path.Next < path.Points.Count - 1)
        {
            bool there = GroundDistanceSq(unit.Position, path.Points[path.Next]) <= reach * reach;
            if (!there && (State.Tick % SightCheckTicks != 0 || !Nav.LineClear(unit.Position, path.Points[path.Next + 1], unit.Radius))) break;
            path.Next++;
        }
        bool last = path.Next == path.Points.Count - 1;
        var point = path.Points[path.Next];
        if (last && Settled(unit, point))
        {
            (unit.Driving, unit.RestGoal) = (false, point);
            return true;
        }
        if (!Step(ref unit, point, final: last) || !last) return false;
        unit.RestGoal = point;
        return true;
    }

    // Toward a point by its kind of movement: to stop there if it's the last, or to drive on through it.
    static bool Step(ref Unit unit, Vector3 point, bool final) =>
        unit.Movement == Movement.Foot ? Walk(ref unit, point) : Drive(ref unit, point, through: !final);

    NavPath PathFor(ref Unit unit)
    {
        if (unit.PathSlot >= 0) return _paths[unit.PathSlot];
        if (_freePaths.TryPop(out int slot)) _paths[slot].Planned = false;
        else
        {
            slot = _paths.Count;
            _paths.Add(new NavPath());
        }
        unit.PathSlot = slot;
        return _paths[slot];
    }

    // A new path to `target`, or the old one confirmed on a changed grid if it's still clear. Straight
    // lines are free; searches share a budget per tick, and false means it has to wait for one (a unit with
    // an old path keeps to it meanwhile).
    bool Plan(ref Unit unit, NavPath path, Vector3 target)
    {
        var nav = Nav!;
        bool sameGoal = path.Planned && GroundDistanceSq(path.Goal, target) <= RepathDistance * RepathDistance;
        if (sameGoal && StillClear(unit, path))
        {
            path.Version = nav.Version;
            return true;
        }
        bool straight = nav.LineClear(unit.Position, target, unit.Radius) && nav.Clearance(target) >= unit.Radius;
        if (!straight)
        {
            if (unit.Owner < 0 || unit.Owner >= _searchesLeft.Length || _searchesLeft[unit.Owner] <= 0) return path.Planned;
            _searchesLeft[unit.Owner]--;
        }
        nav.FindPath(unit.Position, target, unit.Radius, path.Points, out _);
        (path.Next, path.Goal, path.Version, path.Planned) = (0, target, nav.Version, true);
        return true;
    }

    bool StillClear(in Unit unit, NavPath path)
    {
        var nav = Nav!;
        var from = unit.Position;
        for (int i = path.Next; i < path.Points.Count; i++)
        {
            if (!nav.LineClear(from, path.Points[i], unit.Radius)) return false;
            from = path.Points[i];
        }
        return true;
    }

    // Near its goal and up against a unit that already stopped there, for the same goal, closer to it: as
    // good as there, rather than shoving at the crowd for the last few metres. Units sent to spots of their
    // own (a formation) don't settle against each other.
    bool Settled(in Unit unit, Vector3 goal)
    {
        float toGoal = GroundDistanceSq(unit.Position, goal);
        if (toGoal > CrowdRadius * CrowdRadius) return false;
        foreach (var other in State.Units)
        {
            if (other.Id == unit.Id || other.Health <= 0 || other.Current.Kind != UnitOrder.None || other.Movement == Movement.Static) continue;
            if (!(GroundDistanceSq(other.RestGoal, goal) <= RepathDistance * RepathDistance)) continue; // stopped for somewhere else (or NaN: never moved)
            float touch = unit.Radius + other.Radius + CrowdSlack;
            if (GroundDistanceSq(other.Position, unit.Position) <= touch * touch && GroundDistanceSq(other.Position, goal) < toGoal) return true;
        }
        return false;
    }

    void FreePath(in Unit unit)
    {
        if (unit.PathSlot >= 0) _freePaths.Push(unit.PathSlot);
    }

    // ---- Pushing ----

    // Units overlapping each other are pushed apart, a share of the overlap each tick, the lighter and the
    // idle giving way most; then any caught in something solid (a building put up on them, a wall they
    // were shoved into, the map's edge) are pushed back out of it.
    void Separate()
    {
        if (Nav is null) return;
        var units = CollectionsMarshal.AsSpan(State.Units);
        if (_bucketNext.Length < units.Length)
        {
            _bucketNext = new int[Math.Max(units.Length, _bucketNext.Length * 2)];
            _push = new Vector3[_bucketNext.Length];
        }
        Array.Fill(_bucketHead, -1);
        for (int i = 0; i < units.Length; i++)
        {
            _push[i] = Vector3.Zero;
            if (!Pushable(units[i])) continue;
            int b = Bucket(units[i].Position);
            (_bucketNext[i], _bucketHead[b]) = (_bucketHead[b], i);
        }

        for (int i = 0; i < units.Length; i++)
        {
            ref var a = ref units[i];
            if (!Pushable(a)) continue;
            int bx = Math.Clamp((int)((a.Position.X - Nav.MinX) / BucketSize), 0, _bucketsX - 1);
            int bz = Math.Clamp((int)((a.Position.Z - Nav.MinZ) / BucketSize), 0, _bucketsZ - 1);
            for (int z = Math.Max(0, bz - 1); z <= Math.Min(_bucketsZ - 1, bz + 1); z++)
                for (int x = Math.Max(0, bx - 1); x <= Math.Min(_bucketsX - 1, bx + 1); x++)
                    for (int j = _bucketHead[z * _bucketsX + x]; j >= 0; j = _bucketNext[j])
                    {
                        if (j <= i) continue;
                        ref var b = ref units[j];
                        var apart = (a.Position - b.Position) with { Y = 0 };
                        float reach = a.Radius + b.Radius, sq = apart.LengthSquared();
                        if (sq >= reach * reach) continue;
                        float distance = MathF.Sqrt(sq);
                        // Right on top of each other (one just out of the same door): the later one goes on
                        // ahead, off to its right, by its own heading rather than any map direction, so
                        // mirrored sides get mirrored pushes.
                        var direction = distance > 1e-4f ? apart / distance : Forward(b.Heading + 0.5f) * (a.Id < b.Id ? -1 : 1);
                        float overlap = (reach - distance) * PushShare, ga = Give(a), gb = Give(b);
                        _push[i] += Aside(direction, b) * (overlap * ga / (ga + gb));
                        _push[j] += Aside(-direction, a) * (overlap * gb / (ga + gb));
                    }
        }

        for (int i = 0; i < units.Length; i++)
        {
            ref var u = ref units[i];
            if (!Pushable(u)) continue;
            var push = _push[i];
            float length = push.Length();
            if (length > MaxPush) push *= MaxPush / length;
            u.Position += push;
            float clearance = Nav.Clearance(u.Position);
            if (clearance < u.Radius)
            {
                var outward = Nav.Outward(u.Position);
                u.Position += outward * MathF.Min(u.Radius - clearance, MaxUnstick);
            }
            u.Position = Nav.ClampInside(u.Position, MathF.Min(u.Radius, NavGrid.Cell / 2));
            // Shoved well off its spot and nobody on it now: it goes back (idle units do, as after return fire).
            if (u.Current.Kind == UnitOrder.None && !u.Returning && u.RespondTo < 0 && length < 1e-4f
                && GroundDistanceSq(u.Position, u.Anchor) > DriftBack * DriftBack) u.Returning = true;
        }
    }

    static bool Pushable(in Unit u) => u.Health > 0 && u.Movement != Movement.Static;

    // Pushed `away` from a unit under way, a unit also goes to the side of its path (the side it's already
    // on; right in front, to its right), so it's shoved aside rather than ahead, and two meeting head on
    // pass each other.
    static Vector3 Aside(Vector3 away, in Unit pusher)
    {
        if (!pusher.Driving) return away;
        var f = Forward(pusher.Heading);
        var right = new Vector3(f.Z, 0, -f.X);
        float side = Vector3.Dot(right, away);
        var lateral = right * (side < -0.05f ? -1 : 1);
        return Vector3.Normalize(away + lateral);
    }

    // How readily a unit gives way: the smaller, and the one doing nothing, the more (one under way,
    // firing or holding position holds its ground).
    static float Give(in Unit u) => 1 / (MathF.Max(0.1f, u.Radius * u.Radius) * (u.Driving || u.Firing || u.Holding ? MovingMass : 1));

    int Bucket(Vector3 at) =>
        Math.Clamp((int)((at.Z - Nav!.MinZ) / BucketSize), 0, _bucketsZ - 1) * _bucketsX
        + Math.Clamp((int)((at.X - Nav.MinX) / BucketSize), 0, _bucketsX - 1);

    // ---- Placing ----

    // Whether a square footprint (half size `half`, on the grid's axes) at `at` overlaps an obstacle, or
    // sticks out past the map's edge.
    bool OnObstacleOrOffMap(Vector3 at, float half)
    {
        if (Nav is not null && (at.X - half < Nav.MinX || at.X + half > Nav.MaxX || at.Z - half < Nav.MinZ || at.Z + half > Nav.MaxZ)) return true;
        foreach (var o in State.Obstacles)
            if (RectanglesOverlap(at, half, half, 0, o.Position, o.HalfWidth, o.HalfDepth, o.Heading)) return true;
        return false;
    }

    // Two rectangles on the ground, each by its middle, half sizes along its own axes and heading: whether
    // they overlap (separating axis test over both rectangles' axes).
    static bool RectanglesOverlap(Vector3 a, float aw, float ad, float ah, Vector3 b, float bw, float bd, float bh)
    {
        var d = (b - a) with { Y = 0 };
        var (sa, ca) = MathF.SinCos(ah);
        var (sb, cb) = MathF.SinCos(bh);
        Span<Vector3> axes = [new(ca, 0, -sa), new(sa, 0, ca), new(cb, 0, -sb), new(sb, 0, cb)];
        foreach (var axis in axes)
        {
            float ra = aw * MathF.Abs(Vector3.Dot(axes[0], axis)) + ad * MathF.Abs(Vector3.Dot(axes[1], axis));
            float rb = bw * MathF.Abs(Vector3.Dot(axes[2], axis)) + bd * MathF.Abs(Vector3.Dot(axes[3], axis));
            if (MathF.Abs(Vector3.Dot(d, axis)) >= ra + rb) return false;
        }
        return true;
    }
}
