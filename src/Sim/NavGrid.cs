using System.Numerics;

namespace Sim;

/// <summary>
/// The ground as square cells of <see cref="Cell"/> m (half the 2 m building grid), for finding paths and
/// for keeping units out of solid things. Cells are solid under buildings and foundations, posts,
/// defenses and obstacles; beyond the map edge is solid too. Every cell knows its clearance: how far its
/// middle is from the nearest solid cell's edge (negative inside solid ground: how deep). A unit of radius
/// r fits where the clearance is at least r, so one grid serves every size of unit.
///
/// The owner marks what's solid (Clear, Block), then Finish works out the clearances and bumps Version, so
/// paths planned on an older grid know to check themselves. Paths: FindPath, A* over the cells a unit
/// fits in (8 directions, no corner cutting), pulled straight where the way is clear. Nothing allocates
/// once the buffers have grown to fit.
/// </summary>
public sealed class NavGrid
{
    public const float Cell = 1f;
    const float Sqrt2 = 1.41421356f;
    const int GoalSearchCells = 24;    // how far round an unreachable goal it looks for somewhere to stand
    const float LineStep = Cell / 2;   // m between the points LineClear checks
    const float StartSlack = 2f;       // m from its start where a line may run as tight as the start itself

    public readonly float MinX, MinZ;
    public readonly int Width, Depth;  // in cells
    readonly bool[] _solid;
    readonly float[] _clearance;       // m
    public int Version { get; private set; }

    // A*'s buffers, per cell: cost so far, where it came from, and the search that last touched it.
    readonly float[] _cost;
    readonly int[] _from, _seen, _closed;
    int _search;
    int[] _heapCell = new int[1024];
    float[] _heapKey = new float[1024];
    int _heapCount;
    readonly List<int> _cells = [];

    public NavGrid(float minX, float minZ, float maxX, float maxZ)
    {
        (MinX, MinZ) = (minX, minZ);
        Width = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / Cell));
        Depth = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / Cell));
        int n = Width * Depth;
        (_solid, _clearance, _cost) = (new bool[n], new float[n], new float[n]);
        (_from, _seen, _closed) = (new int[n], new int[n], new int[n]);
        Finish();
    }

    public float MaxX => MinX + Width * Cell;
    public float MaxZ => MinZ + Depth * Cell;

    public bool Contains(Vector3 at) => at.X >= MinX && at.X < MaxX && at.Z >= MinZ && at.Z < MaxZ;

    public Vector3 ClampInside(Vector3 at, float margin) =>
        at with { X = Math.Clamp(at.X, MinX + margin, MaxX - margin), Z = Math.Clamp(at.Z, MinZ + margin, MaxZ - margin) };

    // ---- What's solid ----

    public void Clear() => Array.Clear(_solid);

    /// <summary>Marks solid every cell whose middle is inside a rectangle: half sizes along its own axes, turned by `heading`.</summary>
    public void Block(Vector3 center, float halfWidth, float halfDepth, float heading = 0)
    {
        var (sin, cos) = SimMath.SinCos(heading);
        float reach = MathF.Sqrt(halfWidth * halfWidth + halfDepth * halfDepth);
        int x0 = Math.Max(0, (int)MathF.Floor((center.X - reach - MinX) / Cell)), x1 = Math.Min(Width - 1, (int)MathF.Floor((center.X + reach - MinX) / Cell));
        int z0 = Math.Max(0, (int)MathF.Floor((center.Z - reach - MinZ) / Cell)), z1 = Math.Min(Depth - 1, (int)MathF.Floor((center.Z + reach - MinZ) / Cell));
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = MinX + (x + 0.5f) * Cell - center.X, dz = MinZ + (z + 0.5f) * Cell - center.Z;
                // Into the rectangle's own axes: its width runs along (cos, -sin), its depth along (sin, cos).
                float u = dx * cos - dz * sin, v = dx * sin + dz * cos;
                if (MathF.Abs(u) <= halfWidth && MathF.Abs(v) <= halfDepth) _solid[z * Width + x] = true;
            }
    }

    /// <summary>Works out every cell's clearance from what's marked solid, and bumps Version.</summary>
    public void Finish()
    {
        // Two chamfer passes (steps 1 and sqrt 2, in cells): outside, to the nearest solid cell or the map
        // edge; inside, to the nearest open cell. Centre to centre, less half a cell: to the edge.
        Distances(_clearance, open: true);
        for (int i = 0; i < _clearance.Length; i++) _clearance[i] = _solid[i] ? 0 : (_clearance[i] - 0.5f) * Cell;
        Distances(_cost, open: false);
        for (int i = 0; i < _cost.Length; i++) if (_solid[i]) _clearance[i] = -(_cost[i] - 0.5f) * Cell;
        Version++;
    }

    // With `open`, each open cell's distance (in cells) to the nearest solid cell or beyond the edge;
    // otherwise each solid cell's to the nearest open one (the edge doesn't count). The rest get 0.
    void Distances(float[] d, bool open)
    {
        const float Far = 1e9f;
        for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++)
            {
                int i = z * Width + x;
                bool source = open ? _solid[i] : !_solid[i]; // what the distance is measured to
                d[i] = source ? 0 : open && (x == 0 || z == 0 || x == Width - 1 || z == Depth - 1) ? 1 : Far;
            }
        for (int z = 0; z < Depth; z++)
            for (int x = 0; x < Width; x++)
            {
                int i = z * Width + x;
                float v = d[i];
                if (x > 0) v = MathF.Min(v, d[i - 1] + 1);
                if (z > 0)
                {
                    v = MathF.Min(v, d[i - Width] + 1);
                    if (x > 0) v = MathF.Min(v, d[i - Width - 1] + Sqrt2);
                    if (x < Width - 1) v = MathF.Min(v, d[i - Width + 1] + Sqrt2);
                }
                d[i] = v;
            }
        for (int z = Depth - 1; z >= 0; z--)
            for (int x = Width - 1; x >= 0; x--)
            {
                int i = z * Width + x;
                float v = d[i];
                if (x < Width - 1) v = MathF.Min(v, d[i + 1] + 1);
                if (z < Depth - 1)
                {
                    v = MathF.Min(v, d[i + Width] + 1);
                    if (x < Width - 1) v = MathF.Min(v, d[i + Width + 1] + Sqrt2);
                    if (x > 0) v = MathF.Min(v, d[i + Width - 1] + Sqrt2);
                }
                d[i] = v;
            }
    }

    // ---- Queries ----

    public bool IsSolid(Vector3 at) => !Contains(at) || _solid[CellOf(at)];

    /// <summary>
    /// How far `at` is from solid ground, in m, by the cell it's in: negative inside it, and beyond the map
    /// edge, how far beyond.
    /// </summary>
    public float Clearance(Vector3 at)
    {
        if (Contains(at)) return _clearance[CellOf(at)];
        float outX = MathF.Max(MinX - at.X, at.X - MaxX), outZ = MathF.Max(MinZ - at.Z, at.Z - MaxZ);
        return -MathF.Max(0, MathF.Max(outX, outZ)) - Cell / 2;
    }

    int CellOf(Vector3 at) =>
        Math.Clamp((int)((at.Z - MinZ) / Cell), 0, Depth - 1) * Width + Math.Clamp((int)((at.X - MinX) / Cell), 0, Width - 1);

    Vector3 Middle(int cell, float y) => new(MinX + (cell % Width + 0.5f) * Cell, y, MinZ + (cell / Width + 0.5f) * Cell);

    bool Fits(int cell, float radius) => _clearance[cell] >= radius;

    /// <summary>
    /// Which way is out: the direction clearance grows fastest at `at`, on the ground (zero on a flat
    /// stretch). Pushes a unit caught in something solid back out of it.
    /// </summary>
    public Vector3 Outward(Vector3 at)
    {
        float gx = Clearance(at + new Vector3(Cell, 0, 0)) - Clearance(at - new Vector3(Cell, 0, 0));
        float gz = Clearance(at + new Vector3(0, 0, Cell)) - Clearance(at - new Vector3(0, 0, Cell));
        var g = new Vector3(gx, 0, gz);
        return g.LengthSquared() > 1e-6f ? Vector3.Normalize(g) : Vector3.Zero;
    }

    /// <summary>
    /// Whether a unit of `radius` can go straight from `a` to `b`: every point on the way at least that far
    /// from anything solid. Near its start the line may run as tight as the start itself is (a unit pushed
    /// against a wall can still leave along it).
    /// </summary>
    public bool LineClear(Vector3 a, Vector3 b, float radius)
    {
        var delta = (b - a) with { Y = 0 };
        float length = delta.Length();
        float start = MathF.Max(0, MathF.Min(radius, Clearance(a)));
        int steps = (int)MathF.Ceiling(length / LineStep);
        for (int k = 1; k <= steps; k++)
        {
            float t = (float)k / steps;
            float need = t * length <= StartSlack ? start : radius;
            if (Clearance(a + delta * t) < need) return false;
        }
        return true;
    }

    // ---- Paths ----

    /// <summary>
    /// A path for a unit of `radius` from `from` to `to`, as waypoints (not counting `from`) into `path`.
    /// If `to` is somewhere the unit doesn't fit, it goes to the nearest place near it that it does; if
    /// there's no way there at all, as close as it can get. False (and `path` empty) only if the unit has
    /// nowhere to go: boxed in where it stands. `searched` is how many cells the search expanded.
    /// </summary>
    public bool FindPath(Vector3 from, Vector3 to, float radius, List<Vector3> path, out int searched)
    {
        path.Clear();
        searched = 0;
        if (LineClear(from, to, radius) && Clearance(to) >= radius) { path.Add(to); return true; }

        int fromCell = CellOf(ClampInside(from, Cell / 2));
        int start = NearestFit(fromCell, radius, GoalSearchCells, fromCell);
        int goalCell = CellOf(ClampInside(to, Cell / 2));
        int goal = NearestFit(goalCell, radius, GoalSearchCells, start);
        if (start < 0) return false;
        if (goal < 0) goal = goalCell; // nowhere near it to stand: head for it and get as close as the search can

        int reached = Search(start, goal, radius, ref searched);
        _cells.Clear();
        for (int c = reached; c != start; c = _from[c]) _cells.Add(c);
        _cells.Add(start);
        _cells.Reverse();

        // Pull it straight: from each corner, on to the furthest cell still in plain sight.
        var at = from;
        var end = reached == goalCell && Clearance(to) >= radius ? to : Middle(reached, to.Y);
        int i = 0;
        while (true)
        {
            if (LineClear(at, end, radius)) { path.Add(end); break; }
            int far = i;
            while (far + 1 < _cells.Count && LineClear(at, Middle(_cells[far + 1], to.Y), radius)) far++;
            if (far == i && far + 1 < _cells.Count) far++; // nothing in sight (it starts tight): take the next cell anyway
            var corner = Middle(_cells[far], to.Y);
            if (far >= _cells.Count - 1) { path.Add(end); break; }
            path.Add(corner);
            (at, i) = (corner, far);
        }
        return true;
    }

    // A* from `start` toward `goal` over cells a unit of `radius` fits in. Returns the goal, or if it can't
    // be reached, the reached cell nearest it.
    int Search(int start, int goal, float radius, ref int searched)
    {
        _search++;
        int gx = goal % Width, gz = goal / Width;
        (_lineX, _lineZ) = (gx - start % Width, gz - start / Width);
        _heapCount = 0;
        _cost[start] = 0;
        (_from[start], _seen[start]) = (start, _search);
        Push(start, Heuristic(start, gx, gz));
        int best = start;
        float bestH = Heuristic(start, gx, gz);
        while (_heapCount > 0)
        {
            int c = Pop();
            if (_closed[c] == _search) continue;
            _closed[c] = _search;
            searched++;
            float h = Heuristic(c, gx, gz);
            if (h < bestH) (best, bestH) = (c, h);
            if (c == goal) return goal;
            int x = c % Width, z = c / Width;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dz) == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= Width || nz >= Depth) continue;
                    int n = nz * Width + nx;
                    if (_closed[n] == _search || !Fits(n, radius)) continue;
                    // Diagonally only past two open sides, so it never cuts a corner.
                    if (dx != 0 && dz != 0 && (!Fits(z * Width + nx, radius) || !Fits(nz * Width + x, radius))) continue;
                    float cost = _cost[c] + (dx != 0 && dz != 0 ? Sqrt2 : 1);
                    if (_seen[n] == _search && cost >= _cost[n]) continue;
                    (_cost[n], _from[n], _seen[n]) = (cost, c, _search);
                    Push(n, cost + Heuristic(n, gx, gz));
                }
        }
        return best;
    }

    // Octile distance, in cells, nudged a hair (well under a step) by where the cell lies against the
    // straight line from the start to the goal: up for cells off it, so among equally short ways the search
    // prefers the straightest, and down a smaller hair on its right, so a way round either side of
    // something that's exactly as long goes right. Both measure from the line, not the map, so mirrored
    // sides of a map get mirrored paths; ties in the heap would otherwise go by the order cells are
    // visited in, which favours one corner of the map.
    float Heuristic(int cell, int gx, int gz)
    {
        int ox = cell % Width - gx, oz = cell / Width - gz, dx = Math.Abs(ox), dz = Math.Abs(oz);
        float side = (ox * _lineZ - oz * _lineX) / (MathF.Abs(_lineX) + MathF.Abs(_lineZ) + 1);
        return Math.Max(dx, dz) + (Sqrt2 - 1) * Math.Min(dx, dz) + 1e-3f * MathF.Abs(side) - 1e-5f * side;
    }
    int _lineX, _lineZ; // the search's start to goal, in cells

    // A cell near `cell` (itself if it fits) that a unit of `radius` fits in, looking out ring by ring up
    // to `rings` cells away: of the nearest few rings with any, the one nearest `cell` counting half its
    // distance from `toward` too, so a goal inside a building ends on the side the unit comes from. -1 if none.
    int NearestFit(int cell, float radius, int rings, int toward)
    {
        if (Fits(cell, radius)) return cell;
        const int ExtraRings = 2;
        int cx = cell % Width, cz = cell / Width, best = -1, last = rings;
        float bestD = float.MaxValue;
        for (int r = 1; r <= last; r++)
            for (int z = cz - r; z <= cz + r; z++)
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(z - cz)) != r || x < 0 || z < 0 || x >= Width || z >= Depth) continue;
                    int n = z * Width + x;
                    if (!Fits(n, radius)) continue;
                    int tx = x - toward % Width, tz = z - toward / Width;
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz)) + 0.5f * MathF.Sqrt(tx * tx + tz * tz);
                    // Ties go to the right of the way from `toward`, not to whichever comes first in cell order.
                    int wx = cx - toward % Width, wz = cz - toward / Width;
                    d -= 1e-4f * MathF.Sign(wx * (z - cz) - wz * (x - cx));
                    if (d < bestD) (best, bestD) = (n, d);
                    last = Math.Min(last, r + ExtraRings);
                }
        return best;
    }

    void Push(int cell, float key)
    {
        if (_heapCount == _heapCell.Length)
        {
            Array.Resize(ref _heapCell, _heapCount * 2);
            Array.Resize(ref _heapKey, _heapCount * 2);
        }
        int i = _heapCount++;
        while (i > 0)
        {
            int parent = (i - 1) / 2;
            if (_heapKey[parent] <= key) break;
            (_heapCell[i], _heapKey[i]) = (_heapCell[parent], _heapKey[parent]);
            i = parent;
        }
        (_heapCell[i], _heapKey[i]) = (cell, key);
    }

    int Pop()
    {
        int top = _heapCell[0];
        int lastCell = _heapCell[--_heapCount];
        float lastKey = _heapKey[_heapCount];
        int i = 0;
        while (true)
        {
            int child = 2 * i + 1;
            if (child >= _heapCount) break;
            if (child + 1 < _heapCount && _heapKey[child + 1] < _heapKey[child]) child++;
            if (_heapKey[child] >= lastKey) break;
            (_heapCell[i], _heapKey[i]) = (_heapCell[child], _heapKey[child]);
            i = child;
        }
        if (_heapCount > 0) (_heapCell[i], _heapKey[i]) = (lastCell, lastKey);
        return top;
    }
}
