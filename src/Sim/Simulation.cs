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

    public SimState State { get; } = new();

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

    public int AddUnit(int owner, Vector3 position, UnitType type) =>
        AddUnit(owner, position, type.Speed, type.MemberHealth * type.Members, type.MemberDps * type.Members, type.Range, type.Members);

    /// <summary>A unit with explicit stats; health and damage are for the whole squad.</summary>
    public int AddUnit(int owner, Vector3 position, float speed = 5, float maxHealth = 100, float dps = 10, float range = 8, int members = 1)
    {
        int id = _nextId++;
        State.Units.Add(new Unit
        {
            Id = id,
            Owner = owner,
            Position = position,
            PrevPosition = position,
            Speed = speed,
            Range = range,
            MaxMembers = members,
            MemberHealth = maxHealth / members,
            MemberDps = dps / members,
            Health = maxHealth,
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
        // A plain merge always feeds its one output; a switch starts closed until someone takes it.
        junction.Selected = junction.Outputs.Count == 1 ? 0 : -1;
        State.Junctions.Add(junction);
        return index;
    }

    /// <summary>
    /// Adds a gatherer post for `owner` at `position`, pulling from the nearest belt point within
    /// `maxDistance`. Returns its id, or -1 if no belt is that close.
    /// </summary>
    public int AddGatherer(int owner, Vector3 position, float maxDistance)
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
            Health = GathererHealth,
            MaxHealth = GathererHealth,
            LastGrabTick = int.MinValue / 2,
        });
        return id;
    }

    // ---- Queries ----

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

    /// <summary>A living unit or gatherer post by id: where it is and who owns it.</summary>
    public bool TryGetTarget(int id, out Vector3 position, out int owner)
    {
        foreach (var unit in State.Units)
            if (unit.Id == id && unit.Health > 0) { (position, owner) = (unit.Position, unit.Owner); return true; }
        foreach (var post in State.Gatherers)
            if (post.Id == id && post.Health > 0) { (position, owner) = (post.Position, post.Owner); return true; }
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
        _ => order.Target,
    };

    // ---- Tick ----

    /// <summary>Advances one tick. The returned list is reused and stays valid until the next call.</summary>
    public IReadOnlyList<SimEvent> Tick(IReadOnlyList<Command> commands)
    {
        _events.Clear();
        foreach (var command in commands) Apply(command);
        UpdateUnits();
        RemoveDead();
        for (int j = 0; j < State.Junctions.Count; j++) UpdateCapture(j, State.Junctions[j]);
        foreach (var line in State.Belts) MovePackages(line);
        foreach (var junction in State.Junctions) Transfer(junction);
        UpdateGatherers();
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
        }
    }

    // Orders only reach units their issuer owns.
    void Issue(int player, int unitId, Order order, bool queued)
    {
        int i = FindUnit(unitId);
        if (i < 0 || State.Units[i].Owner != player) return;
        ref var unit = ref CollectionsMarshal.AsSpan(State.Units)[i];
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
            unit.Firing = false;
            if (unit.Health <= 0) continue; // killed earlier this tick; removed after the loop

            switch (unit.Current.Kind)
            {
                case UnitOrder.None:
                    AutoFire(ref unit);
                    break;

                case UnitOrder.Move:
                    if (StepToward(ref unit, unit.Current.Target))
                    {
                        _events.Add(new SimEvent(SimEventKind.UnitArrived, unit.Id));
                        Complete(ref unit);
                    }
                    break;

                case UnitOrder.Repair:
                {
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
                    if (!InRange(ref unit, unit.Range)) break;

                    (unit.Firing, unit.FireAt) = (true, unit.Current.Target);
                    segment.Health -= unit.Dps * Dt;
                    if (segment.Health <= 0)
                    {
                        Break(l, s);
                        Complete(ref unit);
                    }
                    break;
                }

                case UnitOrder.Attack:
                    UpdateAttack(ref unit);
                    break;
            }
        }
    }

    // Chase the target into range, then fire until it's gone.
    void UpdateAttack(ref Unit unit)
    {
        int targetId = unit.Current.TargetId;
        if (!TryGetTarget(targetId, out var at, out int owner) || owner == unit.Owner)
        {
            Complete(ref unit);
            return;
        }
        unit.Current = unit.Current with { Target = at with { Y = unit.Position.Y } };
        // The killing shot ends the order at once, so a queued one starts without a wasted tick.
        if (InRange(ref unit, unit.Range) && Fire(ref unit, targetId, at)) Complete(ref unit);
    }

    // An idle unit fires at the nearest enemy in range, units before posts. It doesn't chase, and units
    // with orders don't stop to shoot: move means move.
    void AutoFire(ref Unit unit)
    {
        int best = -1;
        float bestSq = unit.Range * unit.Range;
        var at = Vector3.Zero;
        foreach (var other in State.Units)
        {
            if (other.Owner == unit.Owner || other.Health <= 0) continue;
            float sq = GroundDistanceSq(unit.Position, other.Position);
            if (sq <= bestSq) (best, bestSq, at) = (other.Id, sq, other.Position);
        }
        if (best < 0)
        {
            foreach (var post in State.Gatherers)
            {
                if (post.Owner == unit.Owner || post.Health <= 0) continue;
                float sq = GroundDistanceSq(unit.Position, post.Position);
                if (sq <= bestSq) (best, bestSq, at) = (post.Id, sq, post.Position);
            }
        }
        if (best >= 0) Fire(ref unit, best, at);
    }

    // Returns true if this shot destroyed the target.
    bool Fire(ref Unit unit, int targetId, Vector3 at)
    {
        (unit.Firing, unit.FireAt) = (true, at);
        float damage = unit.Dps * Dt;
        int u = FindUnit(targetId);
        if (u >= 0)
        {
            ref var target = ref CollectionsMarshal.AsSpan(State.Units)[u];
            target.Health -= damage;
            return target.Health <= 0;
        }
        int g = FindGatherer(targetId);
        if (g < 0) return false;
        ref var post = ref CollectionsMarshal.AsSpan(State.Gatherers)[g];
        post.Health -= damage;
        return post.Health <= 0;
    }

    // Walks toward the order target until within `range`; true once there.
    static bool InRange(ref Unit unit, float range)
    {
        if (Vector3.Distance(unit.Position, unit.Current.Target) <= range) return true;
        StepToward(ref unit, unit.Current.Target);
        return false;
    }

    // Returns true on arrival.
    static bool StepToward(ref Unit unit, Vector3 target)
    {
        var toTarget = target - unit.Position;
        float distance = toTarget.Length();
        float step = unit.Speed * Dt;
        if (distance <= step)
        {
            unit.Position = target;
            return true;
        }
        unit.Position += toTarget / distance * step;
        return false;
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
    }

    // A switch changes hands when one side's units hold it, with no enemy there, for CaptureSeconds.
    // Both sides present freezes progress; nobody there, or the owner coming back, drains it.
    void UpdateCapture(int index, Junction junction)
    {
        if (!junction.IsSwitch) return;

        int present = Player.None;
        foreach (var unit in State.Units)
        {
            if (GroundDistanceSq(unit.Position, junction.Position) > CaptureRadius * CaptureRadius) continue;
            if (present == Player.None) present = unit.Owner;
            else if (present != unit.Owner) return; // contested
        }

        float step = Dt / CaptureSeconds;
        if (present != Player.None && present != junction.Owner)
        {
            if (junction.Capturer != present) (junction.Capturer, junction.CaptureProgress) = (present, 0);
            junction.CaptureProgress += step;
            if (junction.CaptureProgress < 1 - 1e-4f) return;
            (junction.Owner, junction.Capturer, junction.CaptureProgress) = (present, Player.None, 0);
            _events.Add(new SimEvent(SimEventKind.JunctionCaptured, index, present));
        }
        else if (junction.CaptureProgress > 0)
        {
            junction.CaptureProgress = MathF.Max(0, junction.CaptureProgress - step);
            if (junction.CaptureProgress == 0) junction.Capturer = Player.None;
        }
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

    // Moves at most one waiting package per tick into the selected output, if its entry is clear.
    // Inputs take turns, so a merge fed faster than its output can carry backs up evenly.
    // A closed switch takes nothing, so its inputs back up.
    void Transfer(Junction junction)
    {
        if (junction.Selected < 0 || junction.Inputs.Count == 0) return;
        var output = State.Belts[junction.Outputs[junction.Selected]];
        if (output.Packages.Count > 0 && output.Packages[^1].Distance < output.Spacing - SpacingSlack) return;

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
            return;
        }
    }

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
        // The slack keeps float rounding from blocking a spawn interval that exactly matches the spacing.
        if (line.Packages.Count > 0 && line.Packages[^1].Distance < line.Spacing - SpacingSlack)
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
