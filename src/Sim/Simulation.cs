using System.Numerics;
using System.Runtime.InteropServices;

namespace Sim;

public sealed class Simulation
{
    public const int TicksPerSecond = 20;
    public const float Dt = 1f / TicksPerSecond;

    public SimState State { get; } = new();

    readonly List<SimEvent> _events = [];
    int _nextId = 1;

    public int AddUnit(Vector3 position, float speed)
    {
        int id = _nextId++;
        State.Units.Add(new Unit { Id = id, Position = position, PrevPosition = position, Speed = speed });
        return id;
    }

    public void AddBeltLine(BezierSegment[] segments, float speed, float spawnIntervalSeconds)
    {
        int ticks = Math.Max(1, (int)MathF.Round(spawnIntervalSeconds * TicksPerSecond));
        State.Belts.Add(new BeltLine(segments, speed, ticks));
    }

    /// <summary>Advances one tick. The returned list is reused and stays valid until the next call.</summary>
    public IReadOnlyList<SimEvent> Tick(IReadOnlyList<Command> commands)
    {
        _events.Clear();
        foreach (var command in commands) Apply(command);
        MoveUnits();
        MovePackages();
        SpawnPackages();
        State.Tick++;
        return _events;
    }

    void Apply(Command command)
    {
        switch (command)
        {
            case MoveCommand move:
                foreach (ref var unit in CollectionsMarshal.AsSpan(State.Units))
                {
                    if (unit.Id != move.UnitId) continue;
                    unit.Target = move.Target;
                    unit.Moving = true;
                }
                break;
        }
    }

    void MoveUnits()
    {
        foreach (ref var unit in CollectionsMarshal.AsSpan(State.Units))
        {
            unit.PrevPosition = unit.Position;
            if (!unit.Moving) continue;

            var toTarget = unit.Target - unit.Position;
            float distance = toTarget.Length();
            float step = unit.Speed * Dt;
            if (distance <= step)
            {
                unit.Position = unit.Target;
                unit.Moving = false;
                _events.Add(new SimEvent(SimEventKind.UnitArrived, unit.Id));
            }
            else
            {
                unit.Position += toTarget / distance * step;
            }
        }
    }

    void MovePackages()
    {
        var packages = State.Packages;
        // Backwards, so swap-removal only moves already-processed packages.
        for (int i = packages.Count - 1; i >= 0; i--)
        {
            var p = packages[i];
            var line = State.Belts[p.Line];
            p.PrevPosition = p.Position;
            p.Distance += line.Speed * Dt;
            while (p.Segment < line.Segments.Length && p.Distance >= line.Segments[p.Segment].Length)
            {
                p.Distance -= line.Segments[p.Segment].Length;
                p.Segment++;
            }

            if (p.Segment == line.Segments.Length)
            {
                _events.Add(new SimEvent(SimEventKind.PackageLost, p.Id));
                packages[i] = packages[^1];
                packages.RemoveAt(packages.Count - 1);
                continue;
            }

            var segment = line.Segments[p.Segment];
            p.Position = segment.PositionAt(p.Distance);
            p.Direction = segment.DirectionAt(p.Distance);
            packages[i] = p;
        }
    }

    void SpawnPackages()
    {
        for (int i = 0; i < State.Belts.Count; i++)
        {
            var line = State.Belts[i];
            if (--line.TicksUntilSpawn > 0) continue;
            line.TicksUntilSpawn = line.SpawnIntervalTicks;

            var first = line.Segments[0];
            var start = first.PositionAt(0);
            State.Packages.Add(new Package
            {
                Id = _nextId++,
                Line = i,
                Position = start,
                PrevPosition = start,
                Direction = first.DirectionAt(0),
            });
        }
    }
}
