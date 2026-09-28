using System.Numerics;

namespace Sim;

/// <summary>One side. Names and colors are presentation and live outside the sim.</summary>
public sealed class Player
{
    public const int None = -1; // the owner of neutral things, and the issuer of scripted commands

    public readonly int Index;
    public int Resources;           // the one spendable currency: 1 per package
    public int Gathered, Collected; // where Resources came from: gatherer posts, ground pickups

    public Player(int index) => Index = index;
}

public enum UnitOrder { None, Move, Repair, AttackSegment, Attack }

/// <summary>
/// What a unit is doing. Segment orders use Line/Segment; Attack uses TargetId (a unit or gatherer post).
/// Target is where the unit heads: for segment orders it's filled in when the order starts, and for
/// Attack it follows the target.
/// </summary>
public readonly record struct Order(UnitOrder Kind, Vector3 Target, int Line = -1, int Segment = -1, int TargetId = -1);

/// <summary>A unit type, as loaded from /data/units.json. Members is 1 for a vehicle.</summary>
public sealed record UnitType(int Members, float Speed, float MemberHealth, float MemberDps, float Range);

/// <summary>
/// A vehicle, or an infantry squad. A squad is one sim entity (one position, one order); its members
/// exist only as slices of a shared health pool. They die one by one as the pool drops, and each loss
/// takes that member's share of the damage output with it.
/// </summary>
public struct Unit
{
    public int Id, Owner;
    public Vector3 Position, PrevPosition; // PrevPosition is last tick's, for view interpolation
    public float Speed, Range;             // m/s, m
    public int MaxMembers;
    public float MemberHealth, MemberDps;
    public float Health;                   // the whole squad's pool
    public bool Firing;                    // this tick; for effects
    public Vector3 FireAt;                 // what it fired at this tick
    public Order Current;
    public Queue<Order> Pending;           // shift-queued orders, started in turn when Current completes

    public readonly float MaxHealth => MemberHealth * MaxMembers;
    public readonly int Members => Health <= 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(Health / MemberHealth - 1e-4f));
    public readonly float Dps => MemberDps * Members;
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

/// <summary>Tuning for one belt line.</summary>
public readonly record struct BeltConfig(
    float Speed,                 // m/s
    float Spacing,               // m; minimum distance between packages along the line
    float SpawnIntervalSeconds,
    float MaxSegmentLength = float.PositiveInfinity, // authored curves are cut into breakable segments this long at most
    float SegmentHealth = 100,
    float SpillLoss = 0);        // 0..1: chance a spilled package is destroyed instead of becoming a pickup

/// <summary>
/// A chain of segments. A line not fed by a junction spawns packages at its start; a line not ending
/// in a junction loses them at its end. The belt moves everything on it at once; packages only queue
/// when something ahead of them is held. A broken segment carries nothing: packages on it, and any
/// that reach it, spill. Belts are neutral: anyone can use, break or repair them.
/// </summary>
public sealed class BeltLine
{
    public readonly BeltSegment[] Segments;
    public readonly float Length;
    public readonly float Speed, Spacing, SpillLoss;
    public readonly int SpawnIntervalTicks;

    public int StartJunction { get; internal set; } = -1; // feeds this line; then it doesn't spawn
    public int EndJunction { get; internal set; } = -1;   // this line hands packages to it

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

    public Vector3 StartPosition => Segments[0].Curve.PositionAt(0);
    public Vector3 EndPosition => Segments[^1].Curve.PositionAt(Segments[^1].Curve.Length);

    /// <summary>Index of the segment containing a distance along the line.</summary>
    public int SegmentAt(float distance)
    {
        for (int i = 0; i < Segments.Length - 1; i++)
            if (distance < Segments[i].End) return i;
        return Segments.Length - 1;
    }

    public Vector3 PositionAt(float distance)
    {
        var segment = Segments[SegmentAt(distance)];
        return segment.Curve.PositionAt(distance - segment.Start);
    }

    public Vector3 DirectionAt(float distance)
    {
        var segment = Segments[SegmentAt(distance)];
        return segment.Curve.DirectionAt(distance - segment.Start);
    }
}

/// <summary>
/// Where belt lines meet. Every input feeds whichever output is selected: several inputs make a merge
/// (they take turns), several outputs make a switch. A switch starts neutral and closed; a side takes it
/// by holding it uncontested, and only its owner sets the live output.
/// </summary>
public sealed class Junction
{
    public readonly Vector3 Position;
    public readonly List<int> Inputs = [], Outputs = []; // line indices
    public int Selected = -1;          // index into Outputs; -1 is closed
    public int Owner = Player.None;    // switches only
    public int Capturer = Player.None; // who CaptureProgress belongs to
    public float CaptureProgress;      // 0..1
    internal int NextInput;            // round-robin, so a merge doesn't starve an input

    public Junction(Vector3 position) => Position = position;

    public bool IsSwitch => Outputs.Count > 1;
}

/// <summary>
/// A post beside the belt with a pull point on it. When idle it grabs a package passing the pull point
/// for its owner, then works for a while; packages passing meanwhile go on downstream.
/// </summary>
public struct Gatherer
{
    public int Id, Owner;
    public Vector3 Position; // the post itself, beside the belt
    public int Line;
    public float Distance;   // pull point along the line
    public float Health, MaxHealth;
    public int ReadyAtTick;  // idle from this tick on
    public int LastGrabTick; // for effects
    public int Gathered;
}

public struct Package
{
    public int Id;
    public int Segment;
    public float Distance; // meters along the whole line
    public Vector3 Position, PrevPosition, Direction;
}

/// <summary>A spilled package on the ground. Whoever's unit walks over it collects it.</summary>
public struct Pickup
{
    public int Id;
    public Vector3 Position;
    public int ExpiresAtTick;
}

public sealed class SimState
{
    public int Tick;
    public readonly List<Player> Players = [];
    public readonly List<Unit> Units = [];
    public readonly List<BeltLine> Belts = [];
    public readonly List<Junction> Junctions = [];
    public readonly List<Gatherer> Gatherers = [];
    public readonly List<Pickup> Pickups = []; // unordered: removal swaps with the last
}
