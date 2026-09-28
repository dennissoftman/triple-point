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

public enum UnitOrder { None, Move, Repair, AttackSegment, Attack, AttackMove }

/// <summary>
/// What a unit is doing. Segment orders use Line/Segment; Attack uses TargetId (a unit or gatherer post).
/// Target is where the unit heads: for segment orders it's filled in when the order starts, and for
/// Attack it follows the target.
/// </summary>
public readonly record struct Order(UnitOrder Kind, Vector3 Target, int Line = -1, int Segment = -1, int TargetId = -1);

/// <summary>
/// How a unit gets around. Foot units walk straight at full speed and turn instantly. Vehicles have a
/// heading, accelerate and brake, and turn at a limited rate: tracked ones pivot almost on the spot,
/// wheeled ones need speed to steer, so they arc.
/// </summary>
public enum Movement { Foot, Wheeled, Tracked }

/// <summary>
/// A unit type, as loaded from /data/units.json. Members is 1 for a vehicle. The rest only matter for
/// vehicles: Acceleration and Braking in m/s² (Braking 0: twice Acceleration); EaseIn and EaseOut in
/// seconds, how long speeding up, braking and turning take to build up to full and to settle (0:
/// instant); TurnRate and TurretTurnRate in degrees per second (TurretTurnRate 0: no turret, aims
/// instantly); ReverseSpeed in m/s (0: can't back up). Id is the type's key in the file, filled in when parsed.
/// </summary>
public sealed record UnitType(
    int Members, float Speed, float MemberHealth, float MemberDps, float Range,
    Movement Movement = Movement.Foot, float Acceleration = 0, float Braking = 0, float EaseIn = 0, float EaseOut = 0,
    float TurnRate = 0, float TurretTurnRate = 0, float ReverseSpeed = 0, bool CanCapture = true, string Id = "");

/// <summary>
/// A vehicle, or an infantry squad. A squad is one sim entity (one position, one order); its members
/// exist only as slices of a shared health pool. They die one by one as the pool drops, and each loss
/// takes that member's share of the damage output with it.
/// </summary>
public struct Unit
{
    public int Id, Owner;
    public string Type;                    // unit type id, as in /data/units.json ("" for ad hoc units)
    public Vector3 Position, PrevPosition; // PrevPosition is last tick's, for view interpolation
    public float Heading, PrevHeading;     // radians; facing (sin h, 0, cos h)
    public float Turret, PrevTurret;       // radians, world yaw like Heading; units without a turret aim instantly
    public float Speed, Range;             // top speed m/s, weapon range m
    public Movement Movement;
    // Vehicles only. Effort is the throttle, eased over EaseIn/EaseOut seconds: -1..1, positive pushing
    // along the heading; it brakes (at Braking) when it opposes the motion, and drives (at Acceleration)
    // otherwise. TurnSpeed eases the same way, up to TurnRate.
    public float Acceleration, Braking;    // m/s² at full effort
    public float EaseIn, EaseOut;          // s
    public float TurnRate, TurretTurnRate; // rad/s; TurretTurnRate 0: no turret
    public float ReverseSpeed;             // m/s; 0 for units that can't back up
    public float CurrentSpeed;             // m/s; negative while reversing
    public float Effort;
    public float TurnSpeed;                // rad/s, signed like Heading
    public bool CanCapture;                // squads take switches; vehicles only deny them
    public bool Driving;                   // moved under power this tick; otherwise a vehicle coasts to a stop
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
/// Where belt lines meet. Several inputs make a merge (they take turns), several outputs make a switch.
/// A switch never stops the stream, it only steers it: neutral, it deals packages out to its outputs in
/// turn; a side takes it by holding it uncontested, which turns it toward that side, and from then on
/// only its owner moves it between outputs.
/// </summary>
public sealed class Junction
{
    public readonly Vector3 Position;
    public readonly List<int> Inputs = [], Outputs = []; // line indices
    public int Selected = -1;          // index into Outputs; -1 on a neutral switch, which splits between them
    public int Owner = Player.None;    // switches only
    public int Capturer = Player.None; // who CaptureProgress belongs to
    public float CaptureProgress;      // 0..1
    internal int NextInput, NextOutput; // round-robin, so a merge doesn't starve an input, and a neutral switch splits evenly

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
