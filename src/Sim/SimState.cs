using System.Numerics;

namespace Sim;

public enum UnitOrder { None, Move, Repair }

public struct Unit
{
    public int Id;
    public Vector3 Position, PrevPosition; // PrevPosition is last tick's, for view interpolation
    public Vector3 Target;
    public float Speed; // m/s
    public UnitOrder Order;
    public int RepairLine, RepairSegment; // valid while Order == Repair
}

public enum SegmentState { Normal, Broken }

public sealed class BeltSegment
{
    public readonly BezierSegment Curve;
    public readonly float Start; // distance along the line where this segment begins
    public float End => Start + Curve.Length;

    public SegmentState State;
    public float RepairProgress; // 0..1 while broken; kept if the repairer leaves

    public BeltSegment(BezierSegment curve, float start) => (Curve, Start) = (curve, start);
}

/// <summary>
/// A chain of segments. Packages spawn at the start and are lost at the end. The belt moves
/// everything on it at once; packages only queue when something ahead of them is held.
/// </summary>
public sealed class BeltLine
{
    public readonly BeltSegment[] Segments;
    public readonly float Length;
    public readonly float Speed;   // m/s
    public readonly float Spacing; // minimum distance between packages along the line
    public readonly int SpawnIntervalTicks;

    public readonly List<Package> Packages = []; // ordered: index 0 is furthest along
    public int Spawned, Lost, BlockedSpawns;
    internal int TicksUntilSpawn = 1;

    public BeltLine(BezierSegment[] curves, float speed, float spacing, int spawnIntervalTicks)
    {
        if (curves.Length == 0) throw new ArgumentException("A belt line needs at least one segment.");
        Segments = new BeltSegment[curves.Length];
        for (int i = 0; i < curves.Length; i++)
        {
            Segments[i] = new BeltSegment(curves[i], Length);
            Length += curves[i].Length;
        }
        (Speed, Spacing, SpawnIntervalTicks) = (speed, spacing, spawnIntervalTicks);
    }
}

public struct Package
{
    public int Id;
    public int Segment;
    public float Distance; // meters along the whole line
    public Vector3 Position, PrevPosition, Direction;
}

public sealed class SimState
{
    public int Tick;
    public readonly List<Unit> Units = [];
    public readonly List<BeltLine> Belts = [];
}
