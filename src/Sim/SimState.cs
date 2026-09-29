using System.Numerics;

namespace Sim;

/// <summary>One side. Names and colors are presentation and live outside the sim.</summary>
public sealed class Player
{
    public const int None = -1; // the owner of neutral things, and the issuer of scripted commands

    public readonly int Index;
    public int Resources;           // the one spendable currency: 1 per package
    public int Gathered, Collected; // where Resources came from: gatherer posts, ground pickups
    public int Spent;               // on construction, production and repair, less refunds
    // End conditions (Simulation.EndConditions). With no buildings left but a rebuild still possible, the
    // grace timer counts down (GraceTicksLeft, -1 while not running), paused on ticks a builder works on
    // one of its foundations. Lost: out of the game, everything it had destroyed.
    public int GraceTicksLeft = -1;
    public bool GracePaused;
    public bool Lost;

    public Player(int index) => Index = index;
}

public enum UnitOrder { None, Move, Repair, AttackSegment, Attack, AttackMove, Build }

/// <summary>
/// What a unit is doing. Segment orders use Line/Segment; Attack uses TargetId (a unit, post or building).
/// Target is where the unit heads: for segment orders it's filled in when the order starts, and for
/// Attack it follows the target. Build puts up a Structure (a building type id) at Target facing Facing,
/// or, with a TargetId, works on that foundation; TargetId is filled in once the foundation is laid.
/// </summary>
public readonly record struct Order(UnitOrder Kind, Vector3 Target, int Line = -1, int Segment = -1, int TargetId = -1,
    string? Structure = null, float Facing = 0);

/// <summary>
/// How a unit gets around. Foot units walk straight at full speed and turn instantly. Vehicles have a
/// heading, accelerate and brake, and turn at a limited rate: tracked ones pivot almost on the spot,
/// wheeled ones need speed to steer, so they arc. Static ones never move: defenses, built in place.
/// </summary>
public enum Movement { Foot, Wheeled, Tracked, Static }

/// <summary>
/// How a weapon's shots reach the target. A bullet hits the moment it's fired (drawn as a tracer); a
/// shell is a projectile that flies at the weapon's ShellSpeed and hits on arrival. Rockets and the like
/// come later as more kinds.
/// </summary>
public enum WeaponKind { Bullet, Shell }

/// <summary>
/// What a shot damages. Direct: only its target. Splash: every enemy within SplashRadius of where it
/// lands, and every open belt segment there (anyone's), less toward the edge; a squad takes it on the
/// share of its footprint the blast covers. No friendly fire on units, posts or buildings, for now.
/// </summary>
public enum HitKind { Direct, Splash }

/// <summary>
/// A weapon type, as loaded from /data/weapons.json (fields: docs/data.md): Damage per shot (per member,
/// for a squad), Reload seconds between shots, Range and MinRange in m, for shells their speed in m/s,
/// for splash its radius in m. A Ballistic shell flies to where its target stood, off by up to Scatter m,
/// instead of homing on it. Id is its key in the file.
/// </summary>
public sealed record WeaponType(WeaponKind Kind, float Damage, float Reload, float Range, float ShellSpeed = 0,
    HitKind Hit = HitKind.Direct, float SplashRadius = 0, float MinRange = 0, bool Ballistic = false, float Scatter = 0,
    string Id = "")
{
    /// <summary>No weapon: builders and the like. It never finds anything in range to shoot.</summary>
    public static readonly WeaponType Unarmed = new(WeaponKind.Bullet, 0, 1, 0, Id: "");

    public float Dps => Damage / Reload;
}

/// <summary>
/// A unit type, as loaded from /data/units.json. Members is 1 for a vehicle; for a squad, every member
/// carries the Weapon (an id in weapons.json, resolved into Gun when parsed). The rest only matter for
/// vehicles: Acceleration and Braking in m/s² (Braking 0: twice Acceleration); EaseIn and EaseOut in
/// seconds, how long speeding up, braking and turning take to build up to full and to settle (0:
/// instant); TurnRate and TurretTurnRate in degrees per second (TurretTurnRate 0: no turret, aims
/// instantly); ReverseSpeed in m/s (0: can't back up). Cost is in Resources, paid over BuildTime seconds of
/// production. Builds lists the building types (ids in buildings.json) it can construct: builders only.
/// RepairSeconds and RepairCost make it repair belt (0: it doesn't): how long and how much a segment from 0 to full takes.
/// StopsToFire: it only fires while standing still (artillery). Fields: docs/data.md.
/// No Weapon: unarmed. Id is the type's key in the file, filled in when parsed.
/// </summary>
public sealed record UnitType(
    int Members, float Speed, float MemberHealth, string? Weapon = null,
    Movement Movement = Movement.Foot, float Acceleration = 0, float Braking = 0, float EaseIn = 0, float EaseOut = 0,
    float TurnRate = 0, float TurretTurnRate = 0, float TurretArc = 0, float ReverseSpeed = 0,
    int Cost = 0, float BuildTime = 0, string[]? Builds = null, float RepairSeconds = 0, int RepairCost = 0,
    bool StopsToFire = false, string Id = "")
{
    [System.Text.Json.Serialization.JsonIgnore] public WeaponType Gun { get; init; } = null!;
    public int BuildTicks => Math.Max(1, (int)MathF.Round(BuildTime * Simulation.TicksPerSecond));
}

/// <summary>
/// What a finished building becomes. A Building stays one (and may produce units); a Post turns into a
/// gatherer post, and must be built beside a belt; a Defense turns into a unit that can't move, of the
/// type named by the building type's Unit.
/// </summary>
public enum BuildingKind { Building, Post, Defense }

/// <summary>
/// A building type, as loaded from /data/buildings.json: Health, Size (m, the side of its square
/// footprint), the unit types it Produces (ids in units.json, resolved into Units when parsed), how many
/// units its queue holds, and for building it, its Cost in Resources paid over BuildTime seconds of a
/// builder's work. Kind and Unit: what it becomes when finished. Id is its key in the file.
/// </summary>
public sealed record BuildingType(float Health, float Size, string[]? Produces = null, int QueueLimit = 5, int Cost = 0,
    float BuildTime = 0, BuildingKind Kind = BuildingKind.Building, string? Unit = null, string Id = "")
{
    [System.Text.Json.Serialization.JsonIgnore] public UnitType[] Units { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public UnitType? Defense { get; init; } // Kind Defense: the unit it becomes
    public int BuildTicks => Math.Max(1, (int)MathF.Round(BuildTime * Simulation.TicksPerSecond));
}

/// <summary>
/// A player's building. Until Built it's a foundation: it grows only on ticks a builder works on it
/// (BuildProgress, paying its cost as it goes), from a tenth of its health to full. One that produces
/// trains the unit at the front of its Queue: each tick of Progress pays its share of the cost, and
/// production stalls while its owner can't pay. A finished unit leaves by the Exit and heads for the
/// Rally point; with Repeat on, its type goes back to the end of the queue.
/// </summary>
public sealed class Building
{
    public readonly int Id, Owner;
    public readonly BuildingType Type;
    public readonly Vector3 Position;
    public readonly float Heading;      // radians, like a unit's; the exit is on this side
    public float Health;
    public readonly List<UnitType> Queue = []; // the front one is in production
    public int Progress, Paid;          // ticks into the front unit, and Resources paid toward it
    public bool Stalled;                // couldn't pay this tick
    public bool Repeat;
    public Vector3 Rally;
    internal int Produced;              // spreads units out around the rally point
    public bool Built;
    public int BuildProgress, BuildPaid; // ticks of a builder's work so far, and Resources paid for them
    public bool BuildStalled;           // a builder was there but its owner couldn't pay this tick
    public int WorkedTick { get; internal set; } = -1; // the last tick a builder worked on it; more builders don't add up

    public Building(int id, int owner, BuildingType type, Vector3 position, float heading, bool built = true) =>
        (Id, Owner, Type, Position, Heading, Built, Health) = (id, owner, type, position, heading, built, built ? type.Health : type.Health / 10);

    public float MaxHealth => Type.Health;
    public Vector3 Exit => Position + new Vector3(MathF.Sin(Heading), 0, MathF.Cos(Heading)) * (Type.Size / 2 + 1.5f);
}

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
    public float Speed;                    // top speed, m/s
    public Movement Movement;
    // Vehicles only. Effort is the throttle, eased over EaseIn/EaseOut seconds: -1..1, positive pushing
    // along the heading; it brakes (at Braking) when it opposes the motion, and drives (at Acceleration)
    // otherwise. TurnSpeed eases the same way, up to TurnRate.
    public float Acceleration, Braking;    // m/s² at full effort
    public float EaseIn, EaseOut;          // s
    public float TurnRate, TurretTurnRate; // rad/s; TurretTurnRate 0: no turret
    public float TurretArc;                // rad either side of the nose the turret reaches; 0: all the way round
    public float ReverseSpeed;             // m/s; 0 for units that can't back up
    public float CurrentSpeed;             // m/s; negative while reversing
    public float Effort;
    public float TurnSpeed;                // rad/s, signed like Heading
    public bool Driving;                   // moved under power this tick; otherwise a vehicle coasts to a stop
    public int MaxMembers;
    public float MemberHealth;
    public float Health;                   // the whole squad's pool
    // The weapon, copied from its type: every living member fires it, so a squad's shot does Damage per member.
    public WeaponKind WeaponKind;
    public float Damage, Range, ShellSpeed; // per shot per member; m; m/s
    public float SplashRadius;             // m; 0 for a direct hit
    public float MinRange, Scatter;        // m: won't fire closer; how far off a ballistic shell may land
    public bool Ballistic;                 // its shells fly to a point instead of homing
    public bool StopsToFire;               // fires only while standing still
    public int ReloadTicks, ReadyAtTick, LastShotTick;
    public bool Firing;                    // engaging something this tick (on target, in range), reloading or not
    public Vector3 FireAt;                 // what it's engaging
    // Return fire. LastAttacker (a unit id, -1 none) is who hit it last, at LastHitTick. RespondTo is the
    // attacker it's answering, chasing it no further than the leash from Anchor, where the response began;
    // Returning, an idle unit walking back there. GaveUpOn is an attacker it left at the leash, so it
    // doesn't bounce on the leash under that attacker's fire; a new order clears it.
    public int LastAttacker, LastHitTick, RespondTo, GaveUpOn;
    public string[]? Builds;               // building type ids it can construct; null: not a builder
    public float RepairSeconds;            // to repair a segment from 0 to full; 0: it doesn't repair
    public int RepairCost;                 // Resources that full repair costs, paid as it goes
    public Vector3 Anchor;
    public bool Returning;
    public Order Current;
    public Queue<Order> Pending;           // shift-queued orders, started in turn when Current completes

    /// <summary>Along the heading, m/s² (positive speeds it up forward); what the effort gives right now.</summary>
    public readonly float CurrentAcceleration =>
        Movement == Movement.Foot ? 0 : Effort * (Effort * CurrentSpeed >= 0 ? Acceleration : Braking);
    /// <summary>Sideways, m/s², toward the left of the heading (the turn's pull; a body leans the other way).</summary>
    public readonly float LateralAcceleration => CurrentSpeed * TurnSpeed;

    public readonly float MaxHealth => MemberHealth * MaxMembers;
    public readonly int Members => Health <= 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(Health / MemberHealth - 1e-4f));
    public readonly float Dps => Damage * Members / (ReloadTicks * Simulation.Dt);
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
    public readonly bool Covered; // can't be damaged, takes no posts (see BeltLine)
    public float RepairCredit { get; internal set; } // health already paid for and not yet repaired

    public BeltSegment(BezierSegment curve, float start, float maxHealth, bool covered = false) =>
        (Curve, Start, MaxHealth, Health, Covered) = (curve, start, maxHealth, maxHealth, covered);
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

    public readonly List<Package> Packages = []; // ordered: index 0 is furthest along
    public int Spawned, Lost, Spilled, Destroyed, BlockedSpawns; // Destroyed counts spills that broke
    internal int TicksUntilSpawn = 1;

    /// <summary>
    /// A line of segments. The first `coveredStart` m and the last `coveredEnd` m are covered: a segment
    /// whose middle lies there can't be damaged and takes no posts (where a belt comes in from beyond the
    /// map, or runs through its owner's back field to where the open belt starts).
    /// </summary>
    public BeltLine(BezierSegment[] curves, BeltConfig config, int ticksPerSecond, float coveredStart = 0, float coveredEnd = 0)
    {
        if (curves.Length == 0) throw new ArgumentException("A belt line needs at least one segment.");
        Segments = new BeltSegment[curves.Length];
        float total = 0;
        foreach (var c in curves) total += c.Length;
        for (int i = 0; i < curves.Length; i++)
        {
            float middle = Length + curves[i].Length / 2;
            bool covered = middle < coveredStart || middle > total - coveredEnd;
            Segments[i] = new BeltSegment(curves[i], Length, config.SegmentHealth, covered);
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

/// <summary>
/// A shell in flight. It homes on its target (a unit or post by TargetId, else the point Target on a belt
/// segment) and hits on arrival; a target that died meanwhile leaves it to land where the target was.
/// </summary>
public struct Projectile
{
    public int Id, Owner, Shooter, TargetId, Line, Segment; // Shooter: the unit that fired it, for return fire
    public Vector3 Position, PrevPosition, Target, Origin; // Origin: where it was fired from, for drawing an arc
    public float Speed, Damage, SplashRadius; // SplashRadius 0: a direct hit
    public bool Ballistic;                    // flies to Target, a point, and never homes
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
    public readonly List<Gatherer> Gatherers = [];
    public readonly List<Building> Buildings = [];
    public readonly List<Pickup> Pickups = []; // unordered: removal swaps with the last
    public readonly List<Projectile> Projectiles = []; // unordered
    public bool GameOver;           // at most one player left standing (Simulation.EndConditions)
    public int Winner = Player.None; // once GameOver: the last player standing, or None for a draw
}
