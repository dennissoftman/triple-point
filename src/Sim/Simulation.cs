using System.Numerics;
using System.Runtime.InteropServices;

namespace Sim;

public sealed class Simulation
{
    public const int TicksPerSecond = 20;
    public const float Dt = 1f / TicksPerSecond;

    // Tuning; moves to /data as it settles.
    public const float RepairRange = 2.5f;          // m from the segment
    public const float RepairSeconds = 5f;          // for one unit, from 0 to full health
    public const float GatherSeconds = 2f;          // a gatherer post's work per package
    public const float GathererHealth = 300f;
    public const float CaptureRadius = 4f;          // m around a switch
    public const float CaptureSeconds = 5f;         // for one side holding a switch uncontested
    public const float CollectRadius = 1.5f;        // m; units collect pickups this close
    public const float PickupLifetimeSeconds = 60f;
    const float GrabReach = 0.5f;                   // m either side of a post's pull point
    const float HoldGap = 0.001f;                   // m short of a line's end where packages wait for a junction
    const float ArrivalSlack = 0.01f;               // m; a front package this close to the end is waiting
    const float SpillMinOffset = 1.3f, SpillMaxOffset = 2.5f; // m to the side of the belt center; clear of its edge
    const float SpillAlongJitter = 0.75f;                     // m along the belt
    const float SpacingSlack = 0.001f;                        // m
    const float MuzzleReach = 1.5f, MuzzleHeight = 1.2f; // m; where shells start, ahead of a vehicle along its turret
    const float HitHeight = 0.5f;                   // m; where shells aim, above the target's feet
    const float SquadFootprint = 1.5f;              // m, radius; the area a squad's members spread over, for splash
    const float SplashEdge = 0.5f;                  // share of full splash damage at the blast's edge
    const float AimTolerance = 0.1f;                // rad (~6°); a turret this close to its target fires
    const float ReturnFireLeash = 15f;              // m from where it began that a unit chases an attacker it can't reach
    const float AssistRadius = 8f;                  // m; idle allies this close to a unit under fire answer it too
    const int HitAttentionTicks = 3 * TicksPerSecond; // a unit with nothing in range keeps its turret on whoever hit it this long
    const float RallySpread = 2.5f;                 // m; produced units stand around the rally point, not on it
    public const float GridCell = 2f;               // m; buildings snap to this grid (SnapToGrid)
    public const float PostReach = 4f;              // m; a post must stand this close to a belt, and pulls from it
    const float BuildReach = 2f;                    // m beyond a footprint's edge that a builder works from
    const float BeltHalfWidth = 0.6f;               // m; buildings keep clear of the belt
    const float PostHalfSize = 0.8f;                // m; a gatherer post's footprint, for keeping buildings clear
    const float JunctionClearance = 1.5f;           // m around a junction's disc
    const float FoundationHealth = 0.1f;            // share of full health a new foundation starts with
    const float VehicleArriveRadius = 0.6f;         // m; vehicles don't park to the centimeter
    const float VehicleParkRadius = 1.2f;           // m; a vehicle at rest this close to its target is there too, rather than creeping up on it
    const float StopMargin = 0.3f;                  // m short of the target that a vehicle eases to a stop
    const float StopHysteresis = 0.6f;              // m; once braking to stop, it only lets go if that would leave it this much further short
    const float SpeedSnap = 0.01f, EffortSnap = 0.05f; // m/s, share; this close to the wanted speed with the effort nearly off is there
    const int MaxStopTicks = 30 * TicksPerSecond;   // bounds the stopping-distance lookahead
    const float TrackedPivotAngle = 0.52f;          // rad (30°); sharper than this, tracks turn nearly on the spot
    const float WheeledSlowAngle = 1.05f;           // rad (60°); sharper than this, wheels slow down to turn
    const float ReverseMaxDistance = 12f;           // m; a stopped vehicle backs up to targets behind it closer than this
    const float ReverseStartAngle = 2.09f;          // rad (120°); "behind" when starting to back up
    const float ReverseKeepAngle = 1.75f;           // rad (100°); keeps backing up while the target is within this of its rear

    public SimState State { get; } = new();

    /// <summary>Building types by id, from buildings.json: what BuildCommand can put up. Set at setup.</summary>
    public IReadOnlyDictionary<string, BuildingType> BuildingTypes { get; set; } = new Dictionary<string, BuildingType>();

    readonly SimRandom _random;
    readonly List<SimEvent> _events = [];
    int _nextId = 1;

    public Simulation(uint seed = 1) => _random = new SimRandom(seed);

    // ---- Setup ----

    public int AddPlayer()
    {
        State.Players.Add(new Player(State.Players.Count));
        return State.Players.Count - 1;
    }

    /// <summary>A unit of a type from /data, facing `heading` (radians, facing (sin h, 0, cos h)).</summary>
    public int AddUnit(int owner, Vector3 position, UnitType type, float heading = 0)
    {
        int id = AddUnit(owner, position, type.Speed, type.MemberHealth * type.Members, 0, 0,
            type.Members, type.Movement, type.Acceleration, type.TurnRate, type.ReverseSpeed, type.CanCapture, heading, type.Id,
            type.Braking, type.EaseIn, type.EaseOut, type.TurretTurnRate, type.Gun);
        CollectionsMarshal.AsSpan(State.Units)[^1].Builds = type.Builds;
        return id;
    }

    /// <summary>
    /// A unit with explicit stats; health is for the whole squad, turn rates are in degrees per second, and
    /// braking 0 is twice the acceleration. See UnitType. Without a `weapon`, it gets a bullet weapon with
    /// `range` that fires every tick, `dps` for the whole squad: steady damage, handy for tests.
    /// </summary>
    public int AddUnit(int owner, Vector3 position, float speed = 5, float maxHealth = 100, float dps = 10, float range = 8,
        int members = 1, Movement movement = Movement.Foot, float acceleration = 0, float turnRate = 0,
        float reverseSpeed = 0, bool canCapture = true, float heading = 0, string type = "",
        float braking = 0, float easeIn = 0, float easeOut = 0, float turretTurnRate = 0, WeaponType? weapon = null)
    {
        weapon ??= new WeaponType(WeaponKind.Bullet, dps / members * Dt, Dt, range);
        int id = _nextId++;
        State.Units.Add(new Unit
        {
            Id = id,
            Owner = owner,
            Type = type,
            Position = position,
            PrevPosition = position,
            Heading = heading,
            PrevHeading = heading,
            Turret = heading,
            PrevTurret = heading,
            Speed = speed,
            Movement = movement,
            Acceleration = acceleration,
            Braking = braking > 0 ? braking : acceleration * 2,
            EaseIn = easeIn,
            EaseOut = easeOut,
            TurnRate = turnRate * MathF.PI / 180,
            TurretTurnRate = turretTurnRate * MathF.PI / 180,
            ReverseSpeed = reverseSpeed,
            CanCapture = canCapture,
            MaxMembers = members,
            MemberHealth = maxHealth / members,
            Health = maxHealth,
            WeaponKind = weapon.Kind,
            Damage = weapon.Damage,
            Range = weapon.Range,
            ShellSpeed = weapon.ShellSpeed,
            SplashRadius = weapon.Hit == HitKind.Splash ? weapon.SplashRadius : 0,
            ReloadTicks = Math.Max(1, (int)MathF.Round(weapon.Reload * TicksPerSecond)),
            LastShotTick = int.MinValue / 2,
            LastAttacker = -1,
            LastHitTick = int.MinValue / 2,
            RespondTo = -1,
            GaveUpOn = -1,
            Pending = new(),
        });
        return id;
    }

    /// <summary>Adds a belt line; each curve is cut into breakable segments no longer than the config allows.</summary>
    public void AddBeltLine(BezierSegment[] curves, BeltConfig config)
    {
        var segments = curves.SelectMany(c => c.Split(config.MaxSegmentLength)).ToArray();
        State.Belts.Add(new BeltLine(segments, config, TicksPerSecond));
    }

    /// <summary>
    /// Adds a junction and attaches every line whose end (as an input) or start (as an output) lies
    /// within `attachRadius`. Add all lines first. Returns the junction index.
    /// </summary>
    public int AddJunction(Vector3 position, float attachRadius)
    {
        int index = State.Junctions.Count;
        var junction = new Junction(position);
        for (int l = 0; l < State.Belts.Count; l++)
        {
            var line = State.Belts[l];
            if (line.EndJunction < 0 && Vector3.Distance(line.EndPosition, position) <= attachRadius)
            {
                junction.Inputs.Add(l);
                line.EndJunction = index;
            }
            if (line.StartJunction < 0 && Vector3.Distance(line.StartPosition, position) <= attachRadius)
            {
                junction.Outputs.Add(l);
                line.StartJunction = index;
            }
        }
        // A plain merge always feeds its one output; a switch starts neutral, splitting between its outputs.
        junction.Selected = junction.Outputs.Count == 1 ? 0 : -1;
        State.Junctions.Add(junction);
        return index;
    }

    /// <summary>
    /// Adds a gatherer post for `owner` at `position`, pulling from the nearest belt point within
    /// `maxDistance`. Returns its id, or -1 if no belt is that close.
    /// </summary>
    public int AddGatherer(int owner, Vector3 position, float maxDistance, float health = GathererHealth)
    {
        if (!FindSegment(position, maxDistance, out int line, out int segment)) return -1;
        var s = State.Belts[line].Segments[segment];
        int id = _nextId++;
        State.Gatherers.Add(new Gatherer
        {
            Id = id,
            Owner = owner,
            Position = position,
            Line = line,
            Distance = s.Start + s.Curve.ClosestDistanceAlong(position, out _),
            Health = health,
            MaxHealth = health,
            LastGrabTick = int.MinValue / 2,
        });
        return id;
    }

    /// <summary>
    /// A building for `owner`, its exit facing `heading`, finished or (not `built`) a foundation. Its rally
    /// point starts at the exit. Returns its id.
    /// </summary>
    public int AddBuilding(int owner, Vector3 position, BuildingType type, float heading = 0, bool built = true)
    {
        var building = new Building(_nextId++, owner, type, position, heading, built);
        building.Rally = building.Exit;
        State.Buildings.Add(building);
        return building.Id;
    }

    // ---- Queries ----

    /// <summary>
    /// Where a building of this size goes near `at`: its footprint's edges on the grid lines, so an even
    /// number of cells across centers on a grid line and an odd number on a cell's middle.
    /// </summary>
    public static Vector3 SnapToGrid(Vector3 at, float size)
    {
        int cells = Math.Max(1, (int)MathF.Round(size / GridCell));
        float offset = cells % 2 == 0 ? 0 : GridCell / 2;
        return new Vector3(MathF.Round((at.X - offset) / GridCell) * GridCell + offset, at.Y,
            MathF.Round((at.Z - offset) / GridCell) * GridCell + offset);
    }

    /// <summary>
    /// Whether a building of this type fits at `at`: its footprint clear of other buildings and
    /// foundations, posts, defenses, junctions and belts. A post must also stand within PostReach of a belt.
    /// Anywhere on the map, for now; factions bring their own rules for where they may build.
    /// </summary>
    public bool CanPlace(BuildingType type, Vector3 at)
    {
        float half = type.Size / 2;
        foreach (var b in State.Buildings)
            if (Overlap(at, half, b.Position, b.Type.Size / 2)) return false;
        foreach (var g in State.Gatherers)
            if (Overlap(at, half, g.Position, PostHalfSize)) return false;
        foreach (var u in State.Units)
            if (u.Movement == Movement.Static && Overlap(at, half, u.Position, GridCell / 2)) return false;
        float corner = half * MathF.Sqrt(2); // the footprint's corners, whichever way it faces
        foreach (var j in State.Junctions)
            if (GroundDistanceSq(j.Position, at) < (corner + JunctionClearance) * (corner + JunctionClearance)) return false;
        if (type.Kind == BuildingKind.Post)
            return !FindSegment(at, half + BeltHalfWidth, out _, out _) && FindSegment(at, PostReach, out _, out _);
        return !FindSegment(at, corner + BeltHalfWidth, out _, out _);
    }

    static bool Overlap(Vector3 a, float aHalf, Vector3 b, float bHalf) =>
        MathF.Abs(a.X - b.X) < aHalf + bHalf && MathF.Abs(a.Z - b.Z) < aHalf + bHalf;

    /// <summary>The junction nearest to a ground point, if one is within `maxDistance`.</summary>
    public bool FindJunction(Vector3 point, float maxDistance, out int junction)
    {
        junction = -1;
        float best = maxDistance * maxDistance;
        for (int j = 0; j < State.Junctions.Count; j++)
        {
            float d = GroundDistanceSq(State.Junctions[j].Position, point);
            if (d <= best) (best, junction) = (d, j);
        }
        return junction >= 0;
    }

    /// <summary>The belt segment nearest to a ground point, if one is within `maxDistance`.</summary>
    public bool FindSegment(Vector3 point, float maxDistance, out int line, out int segment)
    {
        (line, segment) = (-1, -1);
        float best = maxDistance;
        for (int l = 0; l < State.Belts.Count; l++)
        {
            var segments = State.Belts[l].Segments;
            for (int s = 0; s < segments.Length; s++)
            {
                segments[s].Curve.ClosestDistanceAlong(point, out float d);
                if (d <= best) (best, line, segment) = (d, l, s);
            }
        }
        return line >= 0;
    }

    /// <summary>A living unit, gatherer post or building by id: where it is and who owns it.</summary>
    public bool TryGetTarget(int id, out Vector3 position, out int owner)
    {
        foreach (var unit in State.Units)
            if (unit.Id == id && unit.Health > 0) { (position, owner) = (unit.Position, unit.Owner); return true; }
        foreach (var post in State.Gatherers)
            if (post.Id == id && post.Health > 0) { (position, owner) = (post.Position, post.Owner); return true; }
        foreach (var building in State.Buildings)
            if (building.Id == id && building.Health > 0) { (position, owner) = (building.Position, building.Owner); return true; }
        (position, owner) = (default, Player.None);
        return false;
    }

    /// <summary>Where a unit coming from `from` heads for a segment order: the segment's nearest point, on the ground.</summary>
    public Vector3 SegmentPoint(int line, int segment, Vector3 from)
    {
        var curve = State.Belts[line].Segments[segment].Curve;
        return curve.PositionAt(curve.ClosestDistanceAlong(from, out _)) with { Y = from.Y };
    }

    /// <summary>Where a unit coming from `from` walks to for an order; for a plain move, the order's own target.</summary>
    public Vector3 OrderPoint(in Order order, Vector3 from) => order.Kind switch
    {
        UnitOrder.Repair or UnitOrder.AttackSegment => SegmentPoint(order.Line, order.Segment, from),
        UnitOrder.Attack => TryGetTarget(order.TargetId, out var at, out _) ? at with { Y = from.Y } : from,
        UnitOrder.Build when order.TargetId >= 0 => TryGetTarget(order.TargetId, out var site, out _) ? site with { Y = from.Y } : from,
        _ => order.Target,
    };

    // ---- Tick ----

    /// <summary>Advances one tick. The returned list is reused and stays valid until the next call.</summary>
    public IReadOnlyList<SimEvent> Tick(IReadOnlyList<Command> commands)
    {
        _events.Clear();
        foreach (var command in commands) Apply(command);
        UpdateUnits();
        MoveShells();
        RemoveDead();
        for (int j = 0; j < State.Junctions.Count; j++) UpdateCapture(j, State.Junctions[j]);
        foreach (var line in State.Belts) MovePackages(line);
        foreach (var junction in State.Junctions) Transfer(junction);
        UpdateGatherers();
        UpdateConstruction();
        foreach (var building in State.Buildings) UpdateProduction(building);
        foreach (var line in State.Belts) SpawnPackage(line);
        UpdatePickups();
        State.Tick++;
        return _events;
    }

    void Apply(Command command)
    {
        switch (command)
        {
            case MoveCommand m:
                Issue(m.Player, m.UnitId, new Order(UnitOrder.Move, m.Target), m.Queued);
                break;
            case AttackCommand a:
                Issue(a.Player, a.UnitId, new Order(UnitOrder.Attack, default, TargetId: a.TargetId), a.Queued);
                break;
            case AttackMoveCommand am:
                Issue(am.Player, am.UnitId, new Order(UnitOrder.AttackMove, am.Target), am.Queued);
                break;
            case AttackSegmentCommand s:
                Issue(s.Player, s.UnitId, new Order(UnitOrder.AttackSegment, default, s.Line, s.Segment), s.Queued);
                break;
            case RepairSegmentCommand r:
                Issue(r.Player, r.UnitId, new Order(UnitOrder.Repair, default, r.Line, r.Segment), r.Queued);
                break;
            case SetJunctionCommand j:
                SetJunction(j.Player, j.Junction, j.Output);
                break;
            case BreakSegmentCommand b:
                Break(b.Line, b.Segment);
                break;
            case BuildCommand b:
                Issue(b.Player, b.UnitId, new Order(UnitOrder.Build, b.Position, Structure: b.BuildingType, Facing: b.Heading), b.Queued);
                break;
            case ResumeBuildCommand r when OwnBuilding(r.Player, r.BuildingId) is { Built: false }:
                Issue(r.Player, r.UnitId, new Order(UnitOrder.Build, default, TargetId: r.BuildingId), r.Queued);
                break;
            case ProduceCommand p when OwnBuilding(p.Player, p.BuildingId) is { Built: true } building:
                if (building.Queue.Count >= building.Type.QueueLimit) break;
                foreach (var type in building.Type.Units)
                    if (type.Id == p.UnitType) { building.Queue.Add(type); break; }
                break;
            case CancelProductionCommand c when OwnBuilding(c.Player, c.BuildingId) is { } building:
                if (c.Index < 0 || c.Index >= building.Queue.Count) break;
                if (c.Index == 0)
                {
                    State.Players[building.Owner].Resources += building.Paid;
                    (building.Progress, building.Paid, building.Stalled) = (0, 0, false);
                }
                building.Queue.RemoveAt(c.Index);
                break;
            case SetRepeatCommand r when OwnBuilding(r.Player, r.BuildingId) is { } building:
                building.Repeat = r.Repeat;
                break;
            case SetRallyCommand r when OwnBuilding(r.Player, r.BuildingId) is { } building:
                building.Rally = r.Rally;
                break;
        }
    }

    // Commands only reach buildings their issuer owns.
    Building? OwnBuilding(int player, int id)
    {
        foreach (var building in State.Buildings)
            if (building.Id == id) return building.Owner == player ? building : null;
        return null;
    }

    // Walks to the site and works on it. The foundation is laid on arrival, if the spot is still clear
    // (otherwise the order ends: BuildBlocked). It grows only on ticks a builder works on it
    // (UpdateConstruction), and the order ends once it's finished or gone.
    void Construct(ref Unit unit)
    {
        var order = unit.Current;
        Building? site = null;
        if (order.TargetId >= 0 && ((site = FindBuilding(order.TargetId)) is null || site.Built))
        {
            Complete(ref unit);
            return;
        }
        var type = site?.Type ?? BuildingTypes.GetValueOrDefault(order.Structure ?? "");
        if (type is null)
        {
            Complete(ref unit);
            return;
        }
        var at = site?.Position ?? order.Target;
        float reach = type.Size / 2 + BuildReach;
        if (GroundDistanceSq(unit.Position, at) > reach * reach)
        {
            // Head for the footprint's edge on this side, not its middle, so it doesn't end up on top of it.
            var away = (unit.Position - at) with { Y = 0 };
            var stand = at + away / away.Length() * (type.Size / 2 + BuildReach / 2);
            Move(ref unit, stand with { Y = unit.Position.Y });
            return;
        }
        if (site is null)
        {
            if (!CanPlace(type, at))
            {
                _events.Add(new SimEvent(SimEventKind.BuildBlocked, unit.Id));
                Complete(ref unit);
                return;
            }
            int id = AddBuilding(unit.Owner, at, type, order.Facing, built: false);
            unit.Current = order with { TargetId = id };
            site = State.Buildings[^1];
            _events.Add(new SimEvent(SimEventKind.BuildingPlaced, id, unit.Id));
        }
        site.WorkedTick = State.Tick;
    }

    Building? FindBuilding(int id)
    {
        foreach (var building in State.Buildings)
            if (building.Id == id) return building;
        return null;
    }

    // Foundations a builder worked on this tick grow a tick, paying the share of the cost due by then
    // (or stalling while the owner can't), and gain health toward full. A finished post or defense is
    // replaced by what it becomes: a gatherer post, or a unit that can't move.
    void UpdateConstruction()
    {
        for (int i = State.Buildings.Count - 1; i >= 0; i--)
        {
            var site = State.Buildings[i];
            site.BuildStalled = false;
            if (site.Built || site.WorkedTick != State.Tick) continue;
            var owner = State.Players[site.Owner];
            int ticks = site.Type.BuildTicks, due = Due(site.Type.Cost, site.BuildProgress, ticks) - site.BuildPaid;
            if (owner.Resources < due)
            {
                site.BuildStalled = true;
                continue;
            }
            (owner.Resources, site.BuildPaid) = (owner.Resources - due, site.BuildPaid + due);
            site.Health = MathF.Min(site.MaxHealth, site.Health + site.MaxHealth * (1 - FoundationHealth) / ticks);
            if (++site.BuildProgress < ticks) continue;

            site.Built = true;
            int became = site.Id;
            if (site.Type.Kind == BuildingKind.Post)
            {
                State.Buildings.RemoveAt(i);
                became = AddGatherer(site.Owner, site.Position, PostReach, site.MaxHealth);
                if (became >= 0) CollectionsMarshal.AsSpan(State.Gatherers)[^1].Health = site.Health;
            }
            else if (site.Type is { Kind: BuildingKind.Defense, Defense: { } defense })
            {
                State.Buildings.RemoveAt(i);
                became = AddUnit(site.Owner, site.Position, defense, site.Heading);
                ref var unit = ref CollectionsMarshal.AsSpan(State.Units)[^1];
                unit.Health = unit.MaxHealth * site.Health / site.MaxHealth; // damage taken while building stays
            }
            _events.Add(new SimEvent(SimEventKind.BuildingCompleted, site.Id, became));
        }
    }

    // What's due in all, in whole Resources, after `progress + 1` of `ticks` ticks of work on something
    // costing `cost`: paid a share a tick, the total comes out exact.
    static int Due(int cost, int progress, int ticks) => (cost * (progress + 1) + ticks - 1) / ticks;

    // Trains the front unit of the queue: each tick pays the share of the cost due by then (in whole
    // Resources, so the total comes out exact), or stalls until the owner can. A finished unit leaves by
    // the exit for its spot around the rally point; with Repeat on, its type rejoins the back of the queue.
    // Foundations don't produce.
    void UpdateProduction(Building building)
    {
        building.Stalled = false;
        if (!building.Built || building.Queue.Count == 0) return;
        var type = building.Queue[0];
        var owner = State.Players[building.Owner];
        int ticks = type.BuildTicks, due = Due(type.Cost, building.Progress, ticks) - building.Paid;
        if (owner.Resources < due)
        {
            building.Stalled = true;
            return;
        }
        (owner.Resources, building.Paid) = (owner.Resources - due, building.Paid + due);
        if (++building.Progress < ticks) return;

        building.Queue.RemoveAt(0);
        (building.Progress, building.Paid) = (0, 0);
        if (building.Repeat) building.Queue.Add(type);

        int id = AddUnit(building.Owner, building.Exit, type, building.Heading);
        ref var unit = ref CollectionsMarshal.AsSpan(State.Units)[^1];
        int slot = building.Produced++ % 7; // the rally point, then six around it
        float angle = (slot - 1) * MathF.Tau / 6;
        var spot = building.Rally + (slot == 0 ? Vector3.Zero : new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * RallySpread);
        if (GroundDistanceSq(spot, unit.Position) > VehicleParkRadius * VehicleParkRadius) Start(ref unit, new Order(UnitOrder.Move, spot));
        _events.Add(new SimEvent(SimEventKind.UnitProduced, id, building.Id));
    }

    // Orders only reach units their issuer owns.
    void Issue(int player, int unitId, Order order, bool queued)
    {
        int i = FindUnit(unitId);
        if (i < 0 || State.Units[i].Owner != player) return;
        ref var unit = ref CollectionsMarshal.AsSpan(State.Units)[i];
        if (unit.Movement == Movement.Static && order.Kind != UnitOrder.Attack) return; // defenses only aim
        if (order.Kind == UnitOrder.Build && (unit.Builds is null || (order.Structure is not null && Array.IndexOf(unit.Builds, order.Structure) < 0))) return;
        if (!queued)
        {
            unit.Pending.Clear();
            Start(ref unit, order);
        }
        else if (unit.Current.Kind == UnitOrder.None) Start(ref unit, order);
        else unit.Pending.Enqueue(order);
    }

    void Start(ref Unit unit, Order order)
    {
        // Resolved now rather than when issued: a queued segment order starts from wherever the unit ended up.
        unit.Current = order with { Target = OrderPoint(order, unit.Position) };
        (unit.RespondTo, unit.Returning, unit.GaveUpOn) = (-1, false, -1); // a new order ends any return fire
    }

    void Complete(ref Unit unit)
    {
        unit.Current = default;
        if (unit.Pending.TryDequeue(out var next)) Start(ref unit, next);
    }

    void SetJunction(int player, int j, int output)
    {
        var junction = State.Junctions[j];
        if (!junction.IsSwitch || junction.Owner != player) return;
        if (output < 0 || output >= junction.Outputs.Count || output == junction.Selected) return;
        junction.Selected = output;
        _events.Add(new SimEvent(SimEventKind.JunctionSwitched, j, output));
    }

    int FindUnit(int id)
    {
        for (int i = 0; i < State.Units.Count; i++)
            if (State.Units[i].Id == id) return i;
        return -1;
    }

    int FindGatherer(int id)
    {
        for (int i = 0; i < State.Gatherers.Count; i++)
            if (State.Gatherers[i].Id == id) return i;
        return -1;
    }

    void UpdateUnits()
    {
        var units = CollectionsMarshal.AsSpan(State.Units);
        for (int i = 0; i < units.Length; i++)
        {
            ref var unit = ref units[i];
            unit.PrevPosition = unit.Position;
            unit.PrevHeading = unit.Heading;
            unit.PrevTurret = unit.Turret;
            (unit.Firing, unit.Driving) = (false, false);
            if (unit.Health <= 0) continue; // killed earlier this tick; removed after the loop

            switch (unit.Current.Kind)
            {
                case UnitOrder.None:
                    // Stay and fight what's in range; walk back to where it stood after chasing an attacker.
                    if (!ReturnFire(ref unit, units) && !Engage(ref unit) && unit.Returning && Move(ref unit, unit.Anchor))
                        unit.Returning = false;
                    break;

                case UnitOrder.Move:
                    Engage(ref unit); // shoots on the move, but doesn't stop for it
                    if (Move(ref unit, unit.Current.Target))
                    {
                        _events.Add(new SimEvent(SimEventKind.UnitArrived, unit.Id));
                        Complete(ref unit);
                    }
                    break;

                case UnitOrder.Repair:
                {
                    Engage(ref unit);
                    var (l, s) = (unit.Current.Line, unit.Current.Segment);
                    var segment = State.Belts[l].Segments[s];
                    if (segment.Health >= segment.MaxHealth) { Complete(ref unit); break; }
                    if (!InRange(ref unit, RepairRange)) break;

                    segment.Health = MathF.Min(segment.MaxHealth, segment.Health + segment.MaxHealth / RepairSeconds * Dt);
                    if (segment.Health >= segment.MaxHealth)
                    {
                        segment.State = SegmentState.Normal;
                        _events.Add(new SimEvent(SimEventKind.SegmentRepaired, l, s));
                        Complete(ref unit);
                    }
                    break;
                }

                case UnitOrder.AttackSegment:
                {
                    var (l, s) = (unit.Current.Line, unit.Current.Segment);
                    var segment = State.Belts[l].Segments[s];
                    if (segment.State == SegmentState.Broken) { Complete(ref unit); break; }
                    bool onTarget = AimAt(ref unit, unit.Current.Target);
                    if (!InRange(ref unit, unit.Range) || !onTarget) break;

                    if (Fire(ref unit, -1, unit.Current.Target, l, s)) Complete(ref unit);
                    break;
                }

                case UnitOrder.Attack:
                    UpdateAttack(ref unit);
                    break;

                case UnitOrder.Build:
                    Construct(ref unit);
                    break;

                case UnitOrder.AttackMove:
                    // Head for the point, but stop to fight whatever comes into range, or turn on whatever shoots
                    // at it from out of range; then carry on.
                    if (!ReturnFire(ref unit, units) && !Engage(ref unit) && Move(ref unit, unit.Current.Target))
                    {
                        _events.Add(new SimEvent(SimEventKind.UnitArrived, unit.Id));
                        Complete(ref unit);
                    }
                    break;
            }
            if (!unit.Driving) Coast(ref unit);
        }
    }

    // Chase the target into range, then fire until it's gone. The turret swings onto it from the start,
    // while the unit closes in, so it opens fire the moment it's in range rather than stopping to aim.
    void UpdateAttack(ref Unit unit)
    {
        int targetId = unit.Current.TargetId;
        if (!TryGetTarget(targetId, out var at, out int owner) || owner == unit.Owner)
        {
            Complete(ref unit);
            return;
        }
        unit.Current = unit.Current with { Target = at with { Y = unit.Position.Y } };
        bool onTarget = AimAt(ref unit, at);
        // The killing shot ends the order at once, so a queued one starts without a wasted tick.
        if (InRange(ref unit, unit.Range) && onTarget && Fire(ref unit, targetId, at)) Complete(ref unit);
    }

    // Turns the turret to the best enemy in range (FindTarget), or back over the nose if there's none, and
    // fires once the turret is on it. Nothing out of range draws the turret: only an attack order aims
    // ahead. Every unit does this whatever it's doing, idle or on the move (a later exception: heavy or
    // emplaced weapons that must stop first), but none stops or chases for it; attack-move is the order
    // that stops to fight. Returns whether an enemy is in range: what attack-move stops for.
    bool Engage(ref Unit unit)
    {
        if (!FindTarget(unit, out int target, out var at))
        {
            // Being shot is the exception: the turret watches whoever hit it for a while.
            bool watching = State.Tick - unit.LastHitTick <= HitAttentionTicks && TryGetTarget(unit.LastAttacker, out at, out _);
            if (watching) AimAt(ref unit, at);
            else TurnTurret(ref unit, unit.Heading);
            return false;
        }
        if (AimAt(ref unit, at)) Fire(ref unit, target, at);
        return true;
    }

    // Return fire, for idle and attack-move units: hit by an enemy while nothing is in range to shoot back
    // at, a unit chases the attacker into range and fires on it, like an attack order, and idle allies
    // close by join in. It gives up past the leash from where it began (idle units then walk back there),
    // or when the attacker dies. Units holding a switch stay put (Engage keeps their turret on the
    // attacker), and unarmed ones can't answer. True while it's answering, so the order itself waits.
    bool ReturnFire(ref Unit unit, Span<Unit> units)
    {
        if (unit.RespondTo < 0)
        {
            if (!Provoked(unit, out int attacker)) return false;
            Answer(ref unit, attacker);
            foreach (ref var ally in units)
            {
                if (ally.Owner != unit.Owner || ally.Id == unit.Id || ally.Health <= 0 || ally.RespondTo >= 0) continue;
                if (ally.Current.Kind is not (UnitOrder.None or UnitOrder.AttackMove)) continue;
                if (GroundDistanceSq(ally.Position, unit.Position) > AssistRadius * AssistRadius) continue;
                if (CanAnswer(ally, attacker)) Answer(ref ally, attacker);
            }
        }

        if (!TryGetTarget(unit.RespondTo, out var at, out int owner) || owner == unit.Owner)
        {
            (unit.RespondTo, unit.Returning) = (-1, unit.Current.Kind == UnitOrder.None);
            return false;
        }
        if (GroundDistanceSq(unit.Position, unit.Anchor) > ReturnFireLeash * ReturnFireLeash)
        {
            (unit.GaveUpOn, unit.RespondTo, unit.Returning) = (unit.RespondTo, -1, unit.Current.Kind == UnitOrder.None);
            return false;
        }
        bool onTarget = AimAt(ref unit, at);
        if (GroundDistanceSq(unit.Position, at) > unit.Range * unit.Range) Move(ref unit, at with { Y = unit.Position.Y });
        else if (onTarget) Fire(ref unit, unit.RespondTo, at);
        return true;
    }

    // Hit since its last update by an enemy it could answer. One it gave up on counts again once it comes
    // back within the leash.
    bool Provoked(in Unit unit, out int attacker)
    {
        attacker = unit.LastAttacker;
        if (unit.LastHitTick < State.Tick - 1 || !CanAnswer(unit, attacker)) return false;
        return attacker != unit.GaveUpOn || (TryGetTarget(attacker, out var at, out _) &&
            GroundDistanceSq(unit.Position, at) <= ReturnFireLeash * ReturnFireLeash);
    }

    // Armed, mobile, free to leave, nothing in range to fight where it is, and the attacker still alive.
    bool CanAnswer(in Unit unit, int attacker) =>
        unit.Damage > 0 && unit.Speed > 0 && !OnSwitch(unit.Position) && !FindTarget(unit, out _, out _) && TryGetTarget(attacker, out _, out _);

    static void Answer(ref Unit unit, int attacker)
    {
        if (!unit.Returning) unit.Anchor = unit.Position; // one already walking back keeps its original spot
        (unit.RespondTo, unit.Returning) = (attacker, false);
    }

    // Within capture range of a switch: holding it matters more than chasing.
    bool OnSwitch(Vector3 position)
    {
        foreach (var junction in State.Junctions)
            if (junction.IsSwitch && GroundDistanceSq(junction.Position, position) <= CaptureRadius * CaptureRadius) return true;
        return false;
    }

    // The weakest enemy in range: fewest hit points left, so it dies soonest, nearest first on ties.
    // Units come before posts and buildings. Allies near the same enemies pick the same target, so they focus fire
    // without the player clicking.
    bool FindTarget(in Unit unit, out int target, out Vector3 at)
    {
        (target, at) = (-1, Vector3.Zero);
        float bestHealth = float.MaxValue, bestSq = float.MaxValue, rangeSq = unit.Range * unit.Range;
        foreach (var other in State.Units)
        {
            if (other.Owner == unit.Owner || other.Health <= 0) continue;
            float sq = GroundDistanceSq(unit.Position, other.Position);
            if (sq > rangeSq || other.Health > bestHealth || (other.Health == bestHealth && sq >= bestSq)) continue;
            (target, bestHealth, bestSq, at) = (other.Id, other.Health, sq, other.Position);
        }
        if (target >= 0) return true;
        foreach (var post in State.Gatherers)
        {
            if (post.Owner == unit.Owner || post.Health <= 0) continue;
            float sq = GroundDistanceSq(unit.Position, post.Position);
            if (sq > rangeSq || post.Health > bestHealth || (post.Health == bestHealth && sq >= bestSq)) continue;
            (target, bestHealth, bestSq, at) = (post.Id, post.Health, sq, post.Position);
        }
        foreach (var building in State.Buildings)
        {
            if (building.Owner == unit.Owner || building.Health <= 0) continue;
            float sq = GroundDistanceSq(unit.Position, building.Position);
            if (sq > rangeSq || building.Health > bestHealth || (building.Health == bestHealth && sq >= bestSq)) continue;
            (target, bestHealth, bestSq, at) = (building.Id, building.Health, sq, building.Position);
        }
        return target >= 0;
    }

    // Turns the turret toward `yaw` at its turn rate; true once it's on target. Units without a turret
    // (squads) aim instantly.
    static bool TurnTurret(ref Unit unit, float yaw)
    {
        if (unit.TurretTurnRate <= 0)
        {
            unit.Turret = yaw;
            return true;
        }
        float off = WrapAngle(yaw - unit.Turret), step = unit.TurretTurnRate * Dt;
        unit.Turret = WrapAngle(unit.Turret + Math.Clamp(off, -step, step));
        return MathF.Abs(off) - step <= AimTolerance;
    }

    static bool AimAt(ref Unit unit, Vector3 at) =>
        TurnTurret(ref unit, MathF.Atan2(at.X - unit.Position.X, at.Z - unit.Position.Z));

    // Engages a target (a unit or post by id, or else a belt segment) and shoots if the weapon has reloaded:
    // every living member's Damage in one shot. A bullet hits at once; a shell flies (MoveShells). Returns
    // true if this shot destroyed the target, which only a bullet can: a shell's kill lands later.
    bool Fire(ref Unit unit, int targetId, Vector3 at, int line = -1, int segment = -1)
    {
        (unit.Firing, unit.FireAt) = (true, at);
        if (State.Tick < unit.ReadyAtTick) return false;
        (unit.ReadyAtTick, unit.LastShotTick) = (State.Tick + unit.ReloadTicks, State.Tick);
        float damage = unit.Damage * unit.Members;
        if (unit.WeaponKind == WeaponKind.Bullet) return Impact(unit.Owner, unit.Id, targetId, line, segment, at, damage, unit.SplashRadius);

        var muzzle = unit.Position + Forward(unit.Turret) * MuzzleReach + new Vector3(0, MuzzleHeight, 0);
        State.Projectiles.Add(new Projectile
        {
            Id = _nextId++,
            Owner = unit.Owner,
            Shooter = unit.Id,
            TargetId = targetId,
            Line = line,
            Segment = segment,
            Position = muzzle,
            PrevPosition = muzzle,
            Target = at with { Y = HitHeight },
            Speed = unit.ShellSpeed,
            Damage = damage,
            SplashRadius = unit.SplashRadius,
        });
        return false;
    }

    // Shells fly at the target, where it is now while it lives, and hit when they get there. One whose
    // target died on the way lands where it last was, harmlessly.
    void MoveShells()
    {
        var shells = State.Projectiles;
        for (int i = shells.Count - 1; i >= 0; i--)
        {
            var p = shells[i];
            p.PrevPosition = p.Position;
            bool alive = true; // a segment target always is; broken ones just take no damage
            if (p.TargetId >= 0 && (alive = TryGetTarget(p.TargetId, out var at, out _))) p.Target = at with { Y = HitHeight };

            var toTarget = p.Target - p.Position;
            float distance = toTarget.Length(), step = p.Speed * Dt;
            if (distance > step)
            {
                p.Position += toTarget / distance * step;
                shells[i] = p;
                continue;
            }
            if (alive || p.SplashRadius > 0) Impact(p.Owner, p.Shooter, alive ? p.TargetId : -2, p.Line, p.Segment, p.Target, p.Damage, p.SplashRadius);
            _events.Add(new SimEvent(SimEventKind.ShellHit, p.Id));
            shells[i] = shells[^1];
            shells.RemoveAt(shells.Count - 1);
        }
    }

    // A shot by `shooter` (a unit id) landing at `at`: a direct one damages its target; a splash one every
    // enemy of `owner` around (and a belt segment it was aimed at). Target -2: nothing in particular, its
    // target died on the way. True if the aimed-at target was destroyed.
    bool Impact(int owner, int shooter, int targetId, int line, int segment, Vector3 at, float damage, float radius)
    {
        if (radius <= 0) return targetId != -2 && Hit(shooter, targetId, line, segment, damage);
        bool destroyed = targetId == -1 && Hit(shooter, -1, line, segment, damage);
        Splash(owner, shooter, at, damage, radius, targetId, ref destroyed);
        return destroyed;
    }

    // Full damage at the center, falling to SplashEdge of it at the radius. A squad takes it on the members
    // the blast covers: the overlap of the blast with its footprint, by distance across it, each member
    // losing at most its own health.
    void Splash(int owner, int shooter, Vector3 at, float damage, float radius, int targetId, ref bool destroyed)
    {
        foreach (ref var unit in CollectionsMarshal.AsSpan(State.Units))
        {
            if (unit.Owner == owner || unit.Health <= 0) continue;
            float d = MathF.Sqrt(GroundDistanceSq(unit.Position, at));
            float falloff = 1 - (1 - SplashEdge) * MathF.Min(1, d / radius);
            float hit;
            if (unit.MaxMembers > 1)
            {
                float covered = Math.Clamp((radius + SquadFootprint - d) / (2 * SquadFootprint), 0, 1);
                hit = MathF.Min(damage * falloff, unit.MemberHealth) * covered * unit.Members;
            }
            else hit = d <= radius ? damage * falloff : 0;
            if (hit <= 0) continue;
            unit.Health -= hit;
            (unit.LastAttacker, unit.LastHitTick) = (shooter, State.Tick);
            destroyed |= unit.Id == targetId && unit.Health <= 0;
        }
        foreach (ref var post in CollectionsMarshal.AsSpan(State.Gatherers))
        {
            if (post.Owner == owner || post.Health <= 0) continue;
            float d = MathF.Sqrt(GroundDistanceSq(post.Position, at));
            if (d > radius) continue;
            post.Health -= damage * (1 - (1 - SplashEdge) * d / radius);
            destroyed |= post.Id == targetId && post.Health <= 0;
        }
        foreach (var building in State.Buildings)
        {
            if (building.Owner == owner || building.Health <= 0) continue;
            float d = MathF.Sqrt(GroundDistanceSq(building.Position, at));
            if (d > radius) continue;
            building.Health -= damage * (1 - (1 - SplashEdge) * d / radius);
            destroyed |= building.Id == targetId && building.Health <= 0;
        }
    }

    // Damages a unit, post or building by id, or else a belt segment, for `shooter`; true if that destroyed it.
    bool Hit(int shooter, int targetId, int line, int segment, float damage)
    {
        if (targetId < 0)
        {
            var s = State.Belts[line].Segments[segment];
            if (s.State == SegmentState.Broken) return false;
            s.Health -= damage;
            if (s.Health > 0) return false;
            Break(line, segment);
            return true;
        }
        int u = FindUnit(targetId);
        if (u >= 0)
        {
            ref var target = ref CollectionsMarshal.AsSpan(State.Units)[u];
            target.Health -= damage;
            (target.LastAttacker, target.LastHitTick) = (shooter, State.Tick);
            return target.Health <= 0;
        }
        int g = FindGatherer(targetId);
        if (g >= 0)
        {
            ref var post = ref CollectionsMarshal.AsSpan(State.Gatherers)[g];
            post.Health -= damage;
            return post.Health <= 0;
        }
        foreach (var building in State.Buildings)
        {
            if (building.Id != targetId) continue;
            building.Health -= damage;
            return building.Health <= 0;
        }
        return false;
    }

    // Moves toward the order target until within `range`; true once there.
    static bool InRange(ref Unit unit, float range)
    {
        if (Vector3.Distance(unit.Position, unit.Current.Target) <= range) return true;
        Move(ref unit, unit.Current.Target);
        return false;
    }

    // Moves the unit toward `target` this tick, by its kind of movement; true on arrival.
    static bool Move(ref Unit unit, Vector3 target)
    {
        if (unit.Movement == Movement.Static) return false;
        unit.Driving = true;
        return unit.Movement == Movement.Foot ? Walk(ref unit, target) : Drive(ref unit, target);
    }

    // Straight at full speed, turning instantly; arrives exactly.
    static bool Walk(ref Unit unit, Vector3 target)
    {
        var toTarget = target - unit.Position;
        float distance = toTarget.Length();
        if (distance > 0) unit.Heading = MathF.Atan2(toTarget.X, toTarget.Z);
        float step = unit.Speed * Dt;
        if (distance <= step)
        {
            unit.Position = target;
            return true;
        }
        unit.Position += toTarget / distance * step;
        return false;
    }

    // Steers toward the target at a limited turn rate, speeding up, and braking to come to rest near it,
    // all eased (EaseSpeed, Steer). Tracked vehicles crawl while turning sharply, so they pivot almost on
    // the spot; wheeled ones need speed to steer, so from a standstill they drive off in an arc. A vehicle
    // standing still with a close target behind it backs up to it instead of turning around.
    static bool Drive(ref Unit unit, Vector3 target)
    {
        float dx = target.X - unit.Position.X, dz = target.Z - unit.Position.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        float turnForward = WrapAngle(MathF.Atan2(dx, dz) - unit.Heading);
        float turnBackward = WrapAngle(turnForward - MathF.PI); // to point the rear at it
        float speed = MathF.Abs(unit.CurrentSpeed);

        // Decided from (near) standstill, and kept while the target stays roughly behind.
        bool reverse = unit.ReverseSpeed > 0 && (unit.CurrentSpeed < -0.05f
            ? MathF.Abs(turnBackward) < ReverseKeepAngle
            : speed < 0.5f && MathF.Abs(turnForward) > ReverseStartAngle && distance < ReverseMaxDistance);
        float turn = reverse ? turnBackward : turnForward;
        float sharpness = MathF.Abs(turn);

        // Close enough (a vehicle at rest a bit further off counts too, rather than creeping up), or so
        // close that lining up would take a loop: stop here and coast to a halt.
        if (distance <= VehicleArriveRadius || (speed < 0.05f && distance <= VehicleParkRadius)
            || (distance < 2 && sharpness > MathF.PI * 0.6f))
        {
            unit.Driving = false;
            return true;
        }

        float rate = unit.TurnRate;
        if (unit.Movement == Movement.Wheeled) rate *= Math.Clamp(speed / (0.3f * unit.Speed), 0.25f, 1f);
        Steer(ref unit, turn, rate);

        // Brake once the rest of the way is what easing to a stop from here takes, less a margin. Once
        // braking, keep at it unless that would leave it well short: while it turns, the straight-line
        // distance shrinks slower than it rolls, and letting go then would pump the pedals.
        float top = reverse ? unit.ReverseSpeed : unit.Speed;
        float slack = distance - MathF.Abs(StoppingDistance(unit));
        bool braking = unit.Effort * unit.CurrentSpeed < 0;
        float want = slack <= StopMargin + (braking ? StopHysteresis : 0) ? 0 : top;
        if (unit.Movement == Movement.Tracked && sharpness > TrackedPivotAngle) want = MathF.Min(want, top * 0.15f);
        else if (unit.Movement == Movement.Wheeled && sharpness > WheeledSlowAngle) want = MathF.Min(want, top * 0.5f);
        if (reverse) want = -want;

        EaseSpeed(ref unit, want);
        unit.Position += Forward(unit.Heading) * unit.CurrentSpeed * Dt;
        return false;
    }

    // A vehicle that isn't driving this tick rolls on and eases to a stop, and its turn winds down.
    static void Coast(ref Unit unit)
    {
        if (unit.Movement == Movement.Foot) return;
        if (unit.CurrentSpeed != 0 || unit.Effort != 0)
        {
            EaseSpeed(ref unit, 0);
            unit.Position += Forward(unit.Heading) * unit.CurrentSpeed * Dt;
        }
        if (unit.TurnSpeed != 0)
        {
            EaseTurnSpeed(ref unit, 0);
            unit.Heading = WrapAngle(unit.Heading + unit.TurnSpeed * Dt);
        }
    }

    // Moves the speed toward `want` through the effort, which builds up over EaseIn seconds and fades over
    // EaseOut, fading so that it reaches zero just as the speed reaches `want`. So speed changes follow an
    // S curve, gentle at both ends, like something heavy, instead of switching between flat-out rates.
    // With both eases 0 it's plain constant acceleration and braking.
    static void EaseSpeed(ref Unit unit, float want)
    {
        float speed = unit.CurrentSpeed, effort = unit.Effort, gap = want - speed;
        if (MathF.Abs(gap) <= SpeedSnap && MathF.Abs(effort) <= EffortSnap)
        {
            (unit.CurrentSpeed, unit.Effort) = (want, 0);
            return;
        }
        float push = MathF.Sign(gap), pull = Pull(unit, push, speed);
        if (pull <= 0) return; // no engine or no brakes: nothing to do it with
        // The most effort this tick that can still fade out onto `want`. Fading down from e, one step of
        // Dt/EaseOut a tick, adds pull * EaseOut * e * (e + Dt/EaseOut) / 2 to the speed; this solves for e.
        float landing = unit.EaseOut > 0
            ? (MathF.Sqrt(Dt * Dt + 8 * unit.EaseOut * MathF.Abs(gap) / pull) - Dt) / (2 * unit.EaseOut)
            : 1;
        float goal = push * MathF.Min(1, landing);
        if (effort * push > 0 && MathF.Abs(goal) <= MathF.Abs(effort)) effort = goal; // easing out onto it
        else
        {
            bool building = goal * effort >= 0 && MathF.Abs(goal) > MathF.Abs(effort);
            float seconds = building ? unit.EaseIn : unit.EaseOut;
            effort = seconds > 0 ? MoveToward(effort, goal, Dt / seconds) : goal;
        }
        speed += effort * Pull(unit, effort, speed) * Dt;
        if (MathF.Sign(want - speed) != push) (speed, effort) = (want, 0); // there, or about to pass it: hold it
        (unit.CurrentSpeed, unit.Effort) = (speed, effort);
    }

    // The acceleration full effort gives: the engine when it pushes along the motion, the brakes against it.
    static float Pull(in Unit unit, float effort, float speed) => effort * speed >= 0 ? unit.Acceleration : unit.Braking;

    // How far easing to a stop from here would carry the vehicle, along its heading (negative when
    // reversing). Runs the same easing forward, so the prediction is exactly what will happen.
    static float StoppingDistance(in Unit unit)
    {
        var probe = unit;
        float distance = 0;
        for (int i = 0; i < MaxStopTicks && (probe.CurrentSpeed != 0 || probe.Effort != 0); i++)
        {
            EaseSpeed(ref probe, 0);
            distance += probe.CurrentSpeed * Dt;
        }
        return distance;
    }

    // Turns toward a heading `turn` radians away, at up to `rate`. The turn builds up over EaseIn and winds
    // down over EaseOut as the heading comes around, so it settles on it instead of stopping dead.
    static void Steer(ref Unit unit, float turn, float rate)
    {
        // The fastest turn this tick that can still wind down onto the heading, a step of `slowing` a tick.
        float slowing = unit.EaseOut > 0 ? unit.TurnRate / unit.EaseOut * Dt : 0;
        float settle = slowing > 0
            ? (MathF.Sqrt(slowing * slowing + 8 * slowing / Dt * MathF.Abs(turn)) - slowing) / 2
            : float.MaxValue;
        EaseTurnSpeed(ref unit, MathF.Sign(turn) * MathF.Min(rate, settle));
        float step = unit.TurnSpeed * Dt;
        if (step * turn > 0 && MathF.Abs(step) >= MathF.Abs(turn)) (step, unit.TurnSpeed) = (turn, 0); // lands on it
        unit.Heading = WrapAngle(unit.Heading + step);
    }

    // Eases the turn speed toward `want` (rad/s): up over EaseIn, down over EaseOut.
    static void EaseTurnSpeed(ref Unit unit, float want)
    {
        bool building = want * unit.TurnSpeed >= 0 && MathF.Abs(want) > MathF.Abs(unit.TurnSpeed);
        float seconds = building ? unit.EaseIn : unit.EaseOut;
        unit.TurnSpeed = seconds > 0 ? MoveToward(unit.TurnSpeed, want, unit.TurnRate / seconds * Dt) : want;
    }

    static float MoveToward(float from, float to, float step) =>
        from < to ? MathF.Min(to, from + step) : MathF.Max(to, from - step);

    static Vector3 Forward(float heading) => new(MathF.Sin(heading), 0, MathF.Cos(heading));

    // Into (-pi, pi].
    static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        return a > MathF.PI ? a - MathF.Tau : a <= -MathF.PI ? a + MathF.Tau : a;
    }

    void RemoveDead()
    {
        for (int i = State.Units.Count - 1; i >= 0; i--)
        {
            if (State.Units[i].Health > 0) continue;
            _events.Add(new SimEvent(SimEventKind.UnitDied, State.Units[i].Id));
            State.Units.RemoveAt(i);
        }
        for (int i = State.Gatherers.Count - 1; i >= 0; i--)
        {
            if (State.Gatherers[i].Health > 0) continue;
            _events.Add(new SimEvent(SimEventKind.GathererDestroyed, State.Gatherers[i].Id));
            State.Gatherers.RemoveAt(i);
        }
        for (int i = State.Buildings.Count - 1; i >= 0; i--)
        {
            if (State.Buildings[i].Health > 0) continue;
            _events.Add(new SimEvent(SimEventKind.BuildingDestroyed, State.Buildings[i].Id));
            State.Buildings.RemoveAt(i);
        }
    }

    // A switch changes hands when one side holds it, with no enemy there, for CaptureSeconds. Only units
    // that can capture (squads) take it, but any unit, vehicles too, denies it to the enemy: both sides
    // present freezes progress. Nobody capturing, or the owner coming back, drains it.
    void UpdateCapture(int index, Junction junction)
    {
        if (!junction.IsSwitch) return;

        int present = Player.None;
        bool capturing = false;
        foreach (var unit in State.Units)
        {
            if (GroundDistanceSq(unit.Position, junction.Position) > CaptureRadius * CaptureRadius) continue;
            if (present != Player.None && present != unit.Owner) return; // contested
            present = unit.Owner;
            capturing |= unit.CanCapture;
        }

        float step = Dt / CaptureSeconds;
        if (capturing && present != junction.Owner)
        {
            if (junction.Capturer != present) (junction.Capturer, junction.CaptureProgress) = (present, 0);
            junction.CaptureProgress += step;
            if (junction.CaptureProgress < 1 - 1e-4f) return;
            (junction.Owner, junction.Capturer, junction.CaptureProgress) = (present, Player.None, 0);
            _events.Add(new SimEvent(SimEventKind.JunctionCaptured, index, present));
            TurnToward(index, junction, present);
        }
        else if (junction.CaptureProgress > 0)
        {
            junction.CaptureProgress = MathF.Max(0, junction.CaptureProgress - step);
            if (junction.CaptureProgress == 0) junction.Capturer = Player.None;
        }
    }

    // A captured switch turns to its new owner's side: the first output whose stream reaches one of their
    // posts, keeping the current one if it already does. With none of theirs downstream it keeps feeding
    // what it fed, or its first output if it was splitting. Either way it feeds one side, never none.
    void TurnToward(int index, Junction junction, int player)
    {
        int count = junction.Outputs.Count, from = Math.Max(0, junction.Selected), output = from;
        for (int k = 0; k < count; k++)
        {
            int o = (from + k) % count;
            if (!Feeds(junction.Outputs[o], player, State.Junctions.Count)) continue;
            output = o;
            break;
        }
        if (output == junction.Selected) return;
        junction.Selected = output;
        _events.Add(new SimEvent(SimEventKind.JunctionSwitched, index, output));
    }

    // Whether packages on a line can reach one of the player's posts: on it, or past junctions further down
    // (whichever way those are set). `depth` bounds the search on looped networks.
    bool Feeds(int line, int player, int depth)
    {
        foreach (var post in State.Gatherers)
            if (post.Line == line && post.Owner == player) return true;
        int next = State.Belts[line].EndJunction;
        if (next < 0 || depth == 0) return false;
        foreach (int output in State.Junctions[next].Outputs)
            if (Feeds(output, player, depth - 1)) return true;
        return false;
    }

    void Break(int lineIndex, int segmentIndex)
    {
        var line = State.Belts[lineIndex];
        var segment = line.Segments[segmentIndex];
        if (segment.State == SegmentState.Broken) return;
        (segment.State, segment.Health) = (SegmentState.Broken, 0);
        _events.Add(new SimEvent(SimEventKind.SegmentBroken, lineIndex, segmentIndex));

        // Everything on the segment falls off where it is.
        foreach (var p in line.Packages)
            if (p.Segment == segmentIndex) Spill(line, p.Position, p.Direction);
        line.Packages.RemoveAll(p => p.Segment == segmentIndex);
    }

    void MovePackages(BeltLine line)
    {
        var segments = line.Segments;
        var packages = CollectionsMarshal.AsSpan(line.Packages);
        // How far the package ahead lets this one go. A line ending in a junction holds its front
        // package at the end until the junction takes it.
        float limit = HandsOff(line) ? line.Length - HoldGap : float.MaxValue;
        int kept = 0;

        for (int i = 0; i < packages.Length; i++)
        {
            var p = packages[i];
            p.PrevPosition = p.Position;
            p.Distance = MathF.Max(p.Distance, MathF.Min(p.Distance + line.Speed * Dt, limit));
            while (p.Segment < segments.Length && p.Distance >= segments[p.Segment].End) p.Segment++;

            if (p.Segment == segments.Length)
            {
                line.Lost++;
                _events.Add(new SimEvent(SimEventKind.PackageLost, p.Id));
                continue;
            }

            var segment = segments[p.Segment];
            if (segment.State == SegmentState.Broken)
            {
                // Reached a break: falls off at its start.
                Spill(line, segment.Curve.PositionAt(0), segment.Curve.DirectionAt(0));
                continue;
            }

            float along = p.Distance - segment.Start;
            p.Position = segment.Curve.PositionAt(along);
            p.Direction = segment.Curve.DirectionAt(along);
            limit = p.Distance - line.Spacing;
            packages[kept++] = p; // compacting in place keeps the front-to-back order
        }

        line.Packages.RemoveRange(kept, packages.Length - kept);
    }

    bool HandsOff(BeltLine line) => line.EndJunction >= 0 && State.Junctions[line.EndJunction].Outputs.Count > 0;

    // Moves at most one waiting package per tick from an input to an output whose entry is clear. Inputs
    // take turns, so a merge fed faster than its output can carry backs up evenly. A switch feeds the
    // output its owner chose; a neutral one deals packages out to its outputs in turn (skipping one that's
    // backed up), so the stream always flows somewhere.
    void Transfer(Junction junction)
    {
        if (junction.Inputs.Count == 0) return;
        int o = OpenOutput(junction);
        if (o < 0) return;
        var output = State.Belts[junction.Outputs[o]];

        for (int k = 0; k < junction.Inputs.Count; k++)
        {
            int i = (junction.NextInput + k) % junction.Inputs.Count;
            var input = State.Belts[junction.Inputs[i]];
            if (input.Packages.Count == 0 || input.Packages[0].Distance < input.Length - ArrivalSlack) continue;

            var package = input.Packages[0];
            input.Packages.RemoveAt(0);
            output.Packages.Add(new Package
            {
                Id = package.Id,
                Position = output.StartPosition,
                PrevPosition = package.Position,
                Direction = output.DirectionAt(0),
            });
            junction.NextInput = (i + 1) % junction.Inputs.Count;
            junction.NextOutput = (o + 1) % junction.Outputs.Count;
            return;
        }
    }

    // The output the next package goes to, or -1 if its entry isn't clear yet.
    int OpenOutput(Junction junction)
    {
        var outputs = junction.Outputs;
        if (junction.Selected >= 0) return EntryClear(State.Belts[outputs[junction.Selected]]) ? junction.Selected : -1;
        for (int k = 0; k < outputs.Count; k++)
        {
            int o = (junction.NextOutput + k) % outputs.Count;
            if (EntryClear(State.Belts[outputs[o]])) return o;
        }
        return -1;
    }

    // Room for a package at the start of a line. The slack keeps float rounding from blocking a spawn
    // interval that exactly matches the spacing.
    static bool EntryClear(BeltLine line) =>
        line.Packages.Count == 0 || line.Packages[^1].Distance >= line.Spacing - SpacingSlack;

    // An idle post grabs the package nearest its pull point, if one is within reach, then works.
    void UpdateGatherers()
    {
        foreach (ref var g in CollectionsMarshal.AsSpan(State.Gatherers))
        {
            if (State.Tick < g.ReadyAtTick) continue;

            var packages = State.Belts[g.Line].Packages;
            int best = -1;
            float bestDistance = GrabReach;
            for (int i = 0; i < packages.Count; i++)
            {
                float d = MathF.Abs(packages[i].Distance - g.Distance);
                if (d <= bestDistance) (best, bestDistance) = (i, d);
            }
            if (best < 0) continue;

            int packageId = packages[best].Id;
            packages.RemoveAt(best); // keeps the front-to-back order
            (g.ReadyAtTick, g.LastGrabTick) = (State.Tick + (int)(GatherSeconds * TicksPerSecond), State.Tick);
            g.Gathered++;
            var owner = State.Players[g.Owner];
            (owner.Gathered, owner.Resources) = (owner.Gathered + 1, owner.Resources + 1);
            _events.Add(new SimEvent(SimEventKind.PackageGathered, packageId, g.Id));
        }
    }

    void Spill(BeltLine line, Vector3 at, Vector3 direction)
    {
        line.Spilled++;
        // Some break in the fall, so holding a break never captures the whole stream.
        if (_random.Range(0, 1) < line.SpillLoss)
        {
            line.Destroyed++;
            return;
        }

        var side = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        float offset = _random.Range(SpillMinOffset, SpillMaxOffset) * (_random.NextUInt() % 2 == 0 ? 1 : -1);
        var position = at + side * offset + direction * _random.Range(-SpillAlongJitter, SpillAlongJitter);
        State.Pickups.Add(new Pickup
        {
            Id = _nextId++,
            Position = position with { Y = 0 },
            ExpiresAtTick = State.Tick + (int)(PickupLifetimeSeconds * TicksPerSecond),
        });
    }

    void SpawnPackage(BeltLine line)
    {
        if (line.StartJunction >= 0) return; // fed by a junction, not a source
        if (--line.TicksUntilSpawn > 0) return;
        line.TicksUntilSpawn = line.SpawnIntervalTicks;

        // A queue reaching back to the source blocks it; that package never exists.
        if (!EntryClear(line))
        {
            line.BlockedSpawns++;
            return;
        }

        var first = line.Segments[0].Curve;
        var start = first.PositionAt(0);
        line.Packages.Add(new Package
        {
            Id = _nextId++,
            Position = start,
            PrevPosition = start,
            Direction = first.DirectionAt(0),
        });
        line.Spawned++;
    }

    // Whoever's unit is on a pickup gets it; unclaimed ones fade.
    void UpdatePickups()
    {
        var pickups = State.Pickups;
        for (int i = pickups.Count - 1; i >= 0; i--)
        {
            int collector = CollectorOf(pickups[i].Position);
            if (collector != Player.None)
            {
                var player = State.Players[collector];
                (player.Collected, player.Resources) = (player.Collected + 1, player.Resources + 1);
            }
            else if (State.Tick < pickups[i].ExpiresAtTick) continue;
            pickups[i] = pickups[^1];
            pickups.RemoveAt(pickups.Count - 1);
        }
    }

    // The owner of the first unit within collecting distance of a point.
    int CollectorOf(Vector3 point)
    {
        foreach (var unit in State.Units)
            if (GroundDistanceSq(unit.Position, point) <= CollectRadius * CollectRadius) return unit.Owner;
        return Player.None;
    }

    static float GroundDistanceSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
