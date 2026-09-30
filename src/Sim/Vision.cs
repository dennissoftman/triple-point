using System.Numerics;
using System.Runtime.InteropServices;

namespace Sim;

/// <summary>What a player remembers of an enemy building or post it has seen: as it was then.</summary>
public struct Ghost
{
    public int Id, Owner;
    public bool IsPost;           // a gatherer post; otherwise a building (or foundation)
    public BuildingType? Type;    // a building's type (null for a post)
    public Vector3 Position;
    public float Heading;
    public bool Built;
    public float HealthShare;     // 0..1, as last seen
    public int Line;              // a post's belt line and pull point along it; -1 for a building
    public float Distance;
    public int SeenTick;
    internal int Confirmed;       // the vision update that last found it still there
}

/// <summary>
/// One player's side of the fog: which ground cells it sees now (and when it last saw each), the enemy
/// buildings and posts it remembers, and every belt segment as it last saw it.
/// </summary>
public sealed class PlayerVision
{
    internal int[] Seen = [];                     // per cell: the tick it was last seen; int.MinValue: never
    public readonly List<Ghost> Ghosts = [];      // unordered: removal swaps with the last
    internal SegmentState[][] SegmentStates = [];
    internal float[][] SegmentHealth = [];
}

/// <summary>
/// Fog of war: each side sees circles of its units', buildings' and posts' sight on a grid of VisionCell m
/// cells over the map, updated at the end of every tick. Enemy units show only while seen (or for a while
/// after they hit you: a shot gives the shooter away); enemy buildings and posts, once seen, are
/// remembered as ghosts until the player sees their spot again; belt segments are remembered as last seen,
/// except that a break upstream of a player's own post is news to it at once. Units only shoot what their
/// side sees. Off (FogOfWar false, or no navigation) every side sees everything.
/// </summary>
public sealed partial class Simulation
{
    public const float VisionCell = 2f;          // m
    const float RevealSeconds = 3f;               // a unit that hits an enemy is seen by that enemy this long
    public const float DefaultSight = 20f;        // m, for ad hoc units

    /// <summary>Whether fog of war is on. Set at setup; needs navigation (the map's bounds).</summary>
    public bool FogOfWar { get; set; }

    bool Fogged => FogOfWar && Nav is not null;

    readonly List<PlayerVision> _visions = [];
    readonly Dictionary<int, int[]> _discs = [];  // per radius in cells: the half-width of each row of a disc
    int _visionTick = int.MinValue;               // the tick of the last vision update
    int _visionWidth, _visionDepth;
    float _visionMinX, _visionMinZ;
    int _visionUpdates;

    public int VisionWidth => _visionWidth;
    public int VisionDepth => _visionDepth;
    public float VisionMinX => _visionMinX;
    public float VisionMinZ => _visionMinZ;

    /// <summary>A player's fog: what it sees and remembers.</summary>
    public PlayerVision Vision(int player)
    {
        EnsureVisions();
        return _visions[player];
    }

    /// <summary>Whether a player sees a ground point now.</summary>
    public bool Sees(int player, Vector3 at)
    {
        if (!Fogged) return true;
        if (player < 0 || player >= _visions.Count) return false;
        int c = VisionCellOf(at);
        return c >= 0 && _visions[player].Seen[c] == _visionTick;
    }

    /// <summary>Whether a player sees any of a round area: its middle, or a point on its edge each way.</summary>
    public bool SeesArea(int player, Vector3 at, float radius) =>
        Sees(player, at) || Sees(player, at + new Vector3(radius, 0, 0)) || Sees(player, at - new Vector3(radius, 0, 0))
        || Sees(player, at + new Vector3(0, 0, radius)) || Sees(player, at - new Vector3(0, 0, radius));

    /// <summary>Whether a player sees a unit: its own always; an enemy where it sees it, or while that unit is given away by its shots.</summary>
    public bool SeesUnit(int player, in Unit unit) =>
        unit.Owner == player || Sees(player, unit.Position)
        || (unit.RevealSince < State.Tick && unit.RevealUntil >= State.Tick && (unit.RevealMask & (1 << player)) != 0);

    /// <summary>The tick a player last saw a ground point; int.MinValue if never.</summary>
    public int LastSeen(int player, Vector3 at)
    {
        if (!Fogged) return _visionTick;
        int c = VisionCellOf(at);
        return c < 0 || player < 0 || player >= _visions.Count ? int.MinValue : _visions[player].Seen[c];
    }

    /// <summary>A belt segment as a player last saw it.</summary>
    public SegmentState SeenState(int player, int line, int segment) =>
        Fogged && player >= 0 && player < _visions.Count && line < _visions[player].SegmentStates.Length
            ? _visions[player].SegmentStates[line][segment] : State.Belts[line].Segments[segment].State;

    /// <summary>A belt segment's health as a player last saw it.</summary>
    public float SeenHealth(int player, int line, int segment) =>
        Fogged && player >= 0 && player < _visions.Count && line < _visions[player].SegmentHealth.Length
            ? _visions[player].SegmentHealth[line][segment] : State.Belts[line].Segments[segment].Health;

    /// <summary>Whether a segment feeds one of a player's posts: a post of its on the line, downstream of it.</summary>
    public bool FeedsPostOf(int player, int line, int segment)
    {
        float end = State.Belts[line].Segments[segment].End;
        foreach (var post in State.Gatherers)
            if (post.Owner == player && post.Line == line && post.Distance >= end) return true;
        return false;
    }

    /// <summary>
    /// For drawing the fog: 1 where `player` sees the cell now, 0 where not, row by row (x fastest) over
    /// VisionWidth by VisionDepth cells from (VisionMinX, VisionMinZ).
    /// </summary>
    public void CopyVisibility(int player, Span<float> into)
    {
        if (!Fogged || player < 0 || player >= _visions.Count) { into.Fill(1); return; }
        var seen = _visions[player].Seen;
        for (int i = 0; i < seen.Length && i < into.Length; i++) into[i] = seen[i] == _visionTick ? 1 : 0;
    }

    int VisionCellOf(Vector3 at)
    {
        int x = (int)MathF.Floor((at.X - _visionMinX) / VisionCell), z = (int)MathF.Floor((at.Z - _visionMinZ) / VisionCell);
        return x < 0 || z < 0 || x >= _visionWidth || z >= _visionDepth ? -1 : z * _visionWidth + x;
    }

    void EnsureVisions()
    {
        if (Nav is not null && _visionWidth == 0)
        {
            (_visionMinX, _visionMinZ) = (Nav.MinX, Nav.MinZ);
            _visionWidth = Math.Max(1, (int)MathF.Ceiling((Nav.MaxX - Nav.MinX) / VisionCell));
            _visionDepth = Math.Max(1, (int)MathF.Ceiling((Nav.MaxZ - Nav.MinZ) / VisionCell));
        }
        while (_visions.Count < State.Players.Count) _visions.Add(new PlayerVision());
        foreach (var v in _visions)
        {
            if (v.Seen.Length != _visionWidth * _visionDepth)
            {
                v.Seen = new int[_visionWidth * _visionDepth];
                Array.Fill(v.Seen, int.MinValue);
            }
            if (v.SegmentStates.Length != State.Belts.Count)
            {
                // A new line: known as it is now (it's part of the map).
                v.SegmentStates = State.Belts.Select(l => l.Segments.Select(s => s.State).ToArray()).ToArray();
                v.SegmentHealth = State.Belts.Select(l => l.Segments.Select(s => s.Health).ToArray()).ToArray();
            }
        }
    }

    // ---- The update ----

    // Every side's sight is stamped anew, then what it sees updates what it remembers. On the first update,
    // every side learns where everything already standing is: starting bases are known (it's one known map).
    void UpdateVision()
    {
        EnsureVisions();
        _visionTick = State.Tick;
        bool first = _visionUpdates++ == 0;
        if (Fogged)
        {
            foreach (var u in State.Units)
                if (u.Health > 0 && u.Owner >= 0) Stamp(u.Owner, u.Position, u.Sight);
            foreach (var b in State.Buildings)
                if (b.Owner >= 0) Stamp(b.Owner, b.Position, b.Type.Sight + b.Type.Size / 2);
            foreach (var g in State.Gatherers)
                if (g.Owner >= 0) Stamp(g.Owner, g.Position, g.Sight);
        }
        for (int p = 0; p < _visions.Count; p++) Remember(p, first);
    }

    void Stamp(int player, Vector3 at, float sight)
    {
        if (player >= _visions.Count) return;
        var seen = _visions[player].Seen;
        int r = Math.Max(0, (int)MathF.Round(sight / VisionCell));
        if (!_discs.TryGetValue(r, out var rows))
        {
            rows = new int[2 * r + 1];
            for (int dz = -r; dz <= r; dz++) rows[dz + r] = (int)MathF.Floor(MathF.Sqrt(r * r - dz * dz + 0.5f));
            _discs[r] = rows;
        }
        int cx = (int)MathF.Floor((at.X - _visionMinX) / VisionCell), cz = (int)MathF.Floor((at.Z - _visionMinZ) / VisionCell);
        for (int dz = -r; dz <= r; dz++)
        {
            int z = cz + dz;
            if (z < 0 || z >= _visionDepth) continue;
            int half = rows[dz + r], x0 = Math.Max(0, cx - half), x1 = Math.Min(_visionWidth - 1, cx + half);
            for (int x = x0; x <= x1; x++) seen[z * _visionWidth + x] = _visionTick;
        }
    }

    void Remember(int player, bool first)
    {
        var vision = _visions[player];
        foreach (var b in State.Buildings)
            if (b.Owner != player && b.Health > 0 && (first || SeesArea(player, b.Position, b.Type.Size / 2)))
                Upsert(vision, new Ghost
                {
                    Id = b.Id, Owner = b.Owner, Type = b.Type, Position = b.Position, Heading = b.Heading, Built = b.Built,
                    HealthShare = b.Health / b.MaxHealth, Line = -1, SeenTick = State.Tick, Confirmed = _visionUpdates,
                });
        foreach (var g in State.Gatherers)
            if (g.Owner != player && g.Health > 0 && (first || SeesArea(player, g.Position, PostHalfSize)))
                Upsert(vision, new Ghost
                {
                    Id = g.Id, Owner = g.Owner, IsPost = true, Position = g.Position, Built = true, HealthShare = g.Health / g.MaxHealth,
                    Line = g.Line, Distance = g.Distance, SeenTick = State.Tick, Confirmed = _visionUpdates,
                });
        // A remembered one whose spot it sees again, and that isn't there: gone.
        var list = vision.Ghosts;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var ghost = list[i];
            if (ghost.Confirmed == _visionUpdates) continue;
            float half = ghost.IsPost ? PostHalfSize : ghost.Type!.Size / 2;
            if (!SeesArea(player, ghost.Position, half)) continue;
            list[i] = list[^1];
            list.RemoveAt(list.Count - 1);
        }

        for (int l = 0; l < State.Belts.Count; l++)
        {
            var segments = State.Belts[l].Segments;
            var states = vision.SegmentStates[l];
            var health = vision.SegmentHealth[l];
            for (int s = 0; s < segments.Length; s++)
            {
                var seg = segments[s];
                if (!Sees(player, seg.Curve.PositionAt(seg.Curve.Length / 2) with { Y = 0 })) continue;
                (states[s], health[s]) = (seg.State, seg.Health);
            }
        }
    }

    static void Upsert(PlayerVision vision, in Ghost ghost)
    {
        var list = CollectionsMarshal.AsSpan(vision.Ghosts);
        for (int i = 0; i < list.Length; i++)
            if (list[i].Id == ghost.Id) { list[i] = ghost; return; }
        vision.Ghosts.Add(ghost);
    }

    // ---- Rules that use it ----

    // A unit, post or building by id as `player` knows it: seen now (a unit), or seen now or remembered (a
    // post or building). Where it is, if so.
    bool Knows(int player, int id, out Vector3 at)
    {
        at = default;
        if (!Fogged) return TryGetTarget(id, out at, out _);
        foreach (var u in State.Units)
            if (u.Id == id) { at = u.Position; return u.Health > 0 && SeesUnit(player, u); }
        if (player >= 0 && player < _visions.Count)
            foreach (var g in _visions[player].Ghosts)
                if (g.Id == id) { at = g.Position; return true; }
        if (FindTruck(id, out var truck)) { at = truck.Position; return SeesArea(player, truck.Position, TruckRadius); } // seen now, never remembered
        return TryGetTarget(id, out at, out int owner) && owner == player;
    }

    // Whether `player` sees a unit, post, building or truck by id right now (what it may shoot at).
    bool SeesTarget(int player, int id)
    {
        if (!Fogged) return true;
        foreach (var u in State.Units)
            if (u.Id == id) return SeesUnit(player, u);
        foreach (var g in State.Gatherers)
            if (g.Id == id) return g.Owner == player || SeesArea(player, g.Position, PostHalfSize);
        foreach (var b in State.Buildings)
            if (b.Id == id) return b.Owner == player || SeesArea(player, b.Position, b.Type.Size / 2);
        return FindTruck(id, out var truck) && SeesArea(player, truck.Position, TruckRadius);
    }

    // A unit that hits something of `victim`'s is seen by `victim` for a while: its shot gives it away.
    void GiveAway(int shooter, int victim)
    {
        if (victim < 0 || victim >= 31) return;
        int i = FindUnit(shooter);
        if (i < 0) return;
        ref var u = ref CollectionsMarshal.AsSpan(State.Units)[i];
        if (u.Owner == victim) return;
        // From the next tick: within the tick, units act in list order, and whoever came later would see it at once.
        if (u.RevealUntil < State.Tick) (u.RevealMask, u.RevealSince) = (0, State.Tick);
        u.RevealMask |= 1 << victim;
        u.RevealUntil = State.Tick + (int)(RevealSeconds * TicksPerSecond);
    }

    // A break is news at once to every player it cuts off: each one with a post downstream of it.
    void TellCutOff(int line, int segment)
    {
        if (!Fogged) return;
        for (int p = 0; p < _visions.Count; p++)
        {
            if (!FeedsPostOf(p, line, segment) || line >= _visions[p].SegmentStates.Length) continue;
            _visions[p].SegmentStates[line][segment] = SegmentState.Broken;
            _visions[p].SegmentHealth[line][segment] = 0;
        }
    }
}
