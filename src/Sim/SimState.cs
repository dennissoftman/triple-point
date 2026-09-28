using System.Numerics;

namespace Sim;

public enum UnitOrder { None, Move, Repair, Attack }

/// <summary>What a unit is doing. For segment orders, Target is filled in when the order starts.</summary>
public readonly record struct Order(UnitOrder Kind, Vector3 Target, int Line = -1, int Segment = -1);

public struct Unit
{
    public int Id;
    public Vector3 Position, PrevPosition; // PrevPosition is last tick's, for view interpolation
    public float Speed; // m/s
    public float Dps;   // damage per second against segments
    public bool Firing; // this tick; for effects
    public Order Current;
    public Queue<Order> Pending; // shift-queued orders, started in turn when Current completes
}

public enum SegmentState { Normal, Broken }

public sealed class BeltSegment
{
    public readonly BezierSegment Curve;
    public readonly float Start; // distance along the line where this segment begins
    public float End => Start + Curve.Length;

    public readonly float MaxHealth;
    public float Health; // a damaged segment still works; at 0 it breaks and stays broken until fully repaired
    public SegmentState State;

    public BeltSegment(BezierSegment curve, float start, float maxHealth) =>
        (Curve, Start, MaxHealth, Health) = (curve, start, maxHealth, maxHealth);
}

/// <summary>
/// A chain of segments. Packages spawn at the start and are lost at the end. The belt moves
/// everything on it at once; packages only queue when something ahead of them is held.
/// A broken segment carries nothing: packages on it, and any that reach it, spill as pickups.
/// </summary>
/// <summary>Tuning for one belt line.</summary>
public readonly record struct BeltConfig(
    float Speed,                 // m/s
    float Spacing,               // m; minimum distance between packages along the line
    float SpawnIntervalSeconds,
    float MaxSegmentLength = float.PositiveInfinity, // authored curves are cut into breakable segments this long at most
    float SegmentHealth = 100,
    float SpillLoss = 0);        // 0..1: chance a spilled package is destroyed instead of becoming a pickup

public sealed class BeltLine
{
    public readonly BeltSegment[] Segments;
    public readonly float Length;
    public readonly float Speed, Spacing, SpillLoss;
    public readonly int SpawnIntervalTicks;

    public readonly List<Package> Packages = []; // ordered: index 0 is furthest along
    public int Spawned, Lost, Spilled, Destroyed, BlockedSpawns; // Destroyed counts spills that broke
    internal int TicksUntilSpawn = 1;

    public BeltLine(BezierSegment[] curves, BeltConfig config, int ticksPerSecond)
    {
        if (curves.Length == 0) throw new ArgumentException("A belt line needs at least one segment.");
        Segments = new BeltSegment[curves.Length];
        for (int i = 0; i < curves.Length; i++)
        {
            Segments[i] = new BeltSegment(curves[i], Length, config.SegmentHealth);
            Length += curves[i].Length;
        }
        (Speed, Spacing, SpillLoss) = (config.Speed, config.Spacing, config.SpillLoss);
        SpawnIntervalTicks = Math.Max(1, (int)MathF.Round(config.SpawnIntervalSeconds * ticksPerSecond));
    }
}

public struct Package
{
    public int Id;
    public int Segment;
    public float Distance; // meters along the whole line
    public Vector3 Position, PrevPosition, Direction;
}

/// <summary>A spilled package on the ground. Any unit walking over it collects it.</summary>
public struct Pickup
{
    public int Id;
    public Vector3 Position;
    public int ExpiresAtTick;
}

public sealed class SimState
{
    public int Tick;
    public readonly List<Unit> Units = [];
    public readonly List<BeltLine> Belts = [];
    public readonly List<Pickup> Pickups = []; // unordered: removal swaps with the last
    public int Collected; // single player for now; becomes per-player income with the economy
}
