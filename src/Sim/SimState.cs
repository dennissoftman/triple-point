using System.Numerics;

namespace Sim;

public struct Unit
{
    public int Id;
    public Vector3 Position, PrevPosition; // PrevPosition is last tick's, for view interpolation
    public Vector3 Target;
    public float Speed; // m/s
    public bool Moving;
}

/// <summary>A chain of Bezier segments. Packages spawn at the start and are lost at the end.</summary>
public sealed class BeltLine
{
    public readonly BezierSegment[] Segments;
    public readonly float Speed; // m/s
    public readonly int SpawnIntervalTicks;
    internal int TicksUntilSpawn = 1;

    public BeltLine(BezierSegment[] segments, float speed, int spawnIntervalTicks)
    {
        if (segments.Length == 0) throw new ArgumentException("A belt line needs at least one segment.");
        (Segments, Speed, SpawnIntervalTicks) = (segments, speed, spawnIntervalTicks);
    }
}

public struct Package
{
    public int Id;
    public int Line, Segment;
    public float Distance; // meters along the current segment
    public Vector3 Position, PrevPosition, Direction;
}

public sealed class SimState
{
    public int Tick;
    public readonly List<Unit> Units = [];
    public readonly List<BeltLine> Belts = [];
    public readonly List<Package> Packages = []; // unordered: removal swaps with the last
}
