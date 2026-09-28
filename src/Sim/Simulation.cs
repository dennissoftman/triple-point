using System.Numerics;
using System.Runtime.InteropServices;

namespace Sim;

public sealed class Simulation
{
    public const int TicksPerSecond = 20;
    public const float Dt = 1f / TicksPerSecond;

    // Tuning; moves to /data once there are real unit types.
    public const float RepairRange = 2.5f;  // m from the segment
    public const float RepairSeconds = 5f;  // for one unit, from broken to normal
    const float HoldGap = 0.001f;           // how far before a broken segment packages stop

    public SimState State { get; } = new();

    readonly List<SimEvent> _events = [];
    int _nextId = 1;

    public int AddUnit(Vector3 position, float speed)
    {
        int id = _nextId++;
        State.Units.Add(new Unit { Id = id, Position = position, PrevPosition = position, Speed = speed });
        return id;
    }

    public void AddBeltLine(BezierSegment[] curves, float speed, float spacing, float spawnIntervalSeconds)
    {
        int ticks = Math.Max(1, (int)MathF.Round(spawnIntervalSeconds * TicksPerSecond));
        State.Belts.Add(new BeltLine(curves, speed, spacing, ticks));
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

    /// <summary>Advances one tick. The returned list is reused and stays valid until the next call.</summary>
    public IReadOnlyList<SimEvent> Tick(IReadOnlyList<Command> commands)
    {
        _events.Clear();
        foreach (var command in commands) Apply(command);
        UpdateUnits();
        foreach (var line in State.Belts) MovePackages(line);
        for (int i = 0; i < State.Belts.Count; i++) SpawnPackage(State.Belts[i]);
        State.Tick++;
        return _events;
    }

    void Apply(Command command)
    {
        switch (command)
        {
            case MoveCommand move:
            {
                int i = FindUnit(move.UnitId);
                if (i < 0) break;
                ref var unit = ref CollectionsMarshal.AsSpan(State.Units)[i];
                unit.Target = move.Target;
                unit.Order = UnitOrder.Move;
                break;
            }
            case BreakSegmentCommand b:
            {
                var segment = State.Belts[b.Line].Segments[b.Segment];
                if (segment.State == SegmentState.Broken) break;
                segment.State = SegmentState.Broken;
                segment.RepairProgress = 0;
                _events.Add(new SimEvent(SimEventKind.SegmentBroken, b.Line, b.Segment));
                break;
            }
            case RepairSegmentCommand r:
            {
                int i = FindUnit(r.UnitId);
                if (i < 0) break;
                ref var unit = ref CollectionsMarshal.AsSpan(State.Units)[i];
                var curve = State.Belts[r.Line].Segments[r.Segment].Curve;
                // Walk to the nearest point of the segment, staying on the ground.
                var target = curve.PositionAt(curve.ClosestDistanceAlong(unit.Position, out _));
                unit.Target = target with { Y = unit.Position.Y };
                (unit.Order, unit.RepairLine, unit.RepairSegment) = (UnitOrder.Repair, r.Line, r.Segment);
                break;
            }
        }
    }

    int FindUnit(int id)
    {
        for (int i = 0; i < State.Units.Count; i++)
            if (State.Units[i].Id == id) return i;
        return -1;
    }

    void UpdateUnits()
    {
        foreach (ref var unit in CollectionsMarshal.AsSpan(State.Units))
        {
            unit.PrevPosition = unit.Position;
            switch (unit.Order)
            {
                case UnitOrder.Move:
                    if (StepTowardTarget(ref unit))
                    {
                        unit.Order = UnitOrder.None;
                        _events.Add(new SimEvent(SimEventKind.UnitArrived, unit.Id));
                    }
                    break;

                case UnitOrder.Repair:
                    var segment = State.Belts[unit.RepairLine].Segments[unit.RepairSegment];
                    if (segment.State != SegmentState.Broken) { unit.Order = UnitOrder.None; break; }
                    if (Vector3.Distance(unit.Position, unit.Target) > RepairRange) { StepTowardTarget(ref unit); break; }

                    segment.RepairProgress += Dt / RepairSeconds;
                    if (segment.RepairProgress >= 1)
                    {
                        (segment.State, segment.RepairProgress) = (SegmentState.Normal, 0);
                        unit.Order = UnitOrder.None;
                        _events.Add(new SimEvent(SimEventKind.SegmentRepaired, unit.RepairLine, unit.RepairSegment));
                    }
                    break;
            }
        }
    }

    // Returns true on arrival.
    static bool StepTowardTarget(ref Unit unit)
    {
        var toTarget = unit.Target - unit.Position;
        float distance = toTarget.Length();
        float step = unit.Speed * Dt;
        if (distance <= step)
        {
            unit.Position = unit.Target;
            return true;
        }
        unit.Position += toTarget / distance * step;
        return false;
    }

    void MovePackages(BeltLine line)
    {
        var segments = line.Segments;
        var packages = CollectionsMarshal.AsSpan(line.Packages);
        float limit = float.MaxValue; // how far the package ahead lets this one go
        int lost = 0;

        for (int i = 0; i < packages.Length; i++)
        {
            ref var p = ref packages[i];
            p.PrevPosition = p.Position;

            float next = segments[p.Segment].State == SegmentState.Broken ? p.Distance : p.Distance + line.Speed * Dt;
            next = MathF.Min(next, limit);
            // A broken segment ahead holds packages at its start.
            for (int s = p.Segment; s + 1 < segments.Length && next >= segments[s].End; s++)
            {
                if (segments[s + 1].State != SegmentState.Broken) continue;
                next = MathF.Min(next, segments[s].End - HoldGap);
                break;
            }
            p.Distance = MathF.Max(next, p.Distance);
            limit = p.Distance - line.Spacing;

            while (p.Segment < segments.Length && p.Distance >= segments[p.Segment].End) p.Segment++;
            if (p.Segment == segments.Length)
            {
                // Packages stay ordered, so the lost ones are always a prefix of the list.
                lost++;
                _events.Add(new SimEvent(SimEventKind.PackageLost, p.Id));
                continue;
            }

            var curve = segments[p.Segment].Curve;
            float along = p.Distance - segments[p.Segment].Start;
            p.Position = curve.PositionAt(along);
            p.Direction = curve.DirectionAt(along);
        }

        if (lost > 0) line.Packages.RemoveRange(0, lost);
        line.Lost += lost;
    }

    void SpawnPackage(BeltLine line)
    {
        if (--line.TicksUntilSpawn > 0) return;
        line.TicksUntilSpawn = line.SpawnIntervalTicks;

        // A queue reaching back to the source blocks it; that package never exists.
        if (line.Packages.Count > 0 && line.Packages[^1].Distance < line.Spacing)
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
}
