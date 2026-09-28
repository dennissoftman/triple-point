using System.Numerics;

namespace Sim.Tests;

public class SimulationTests
{
    static readonly Command[] NoCommands = [];

    // Evenly spaced control points on a line: t advances uniformly with distance.
    static BezierSegment Straight(Vector3 from, Vector3 to) =>
        new(from, Vector3.Lerp(from, to, 1 / 3f), Vector3.Lerp(from, to, 2 / 3f), to);

    [Fact]
    public void Unit_moves_to_target_and_arrives()
    {
        var sim = new Simulation();
        int id = sim.AddUnit(Vector3.Zero, speed: 5);
        var target = new Vector3(10, 0, 0);

        int arrivedAt = -1;
        var events = sim.Tick([new MoveCommand(id, target)]);
        for (int tick = 1; tick <= 100 && arrivedAt < 0; tick++)
        {
            if (events.Contains(new SimEvent(SimEventKind.UnitArrived, id))) arrivedAt = tick;
            else events = sim.Tick(NoCommands);
        }

        // 10 m at 5 m/s is 2 s = 40 ticks; allow one tick of float slack.
        Assert.InRange(arrivedAt, 40, 41);
        Assert.Equal(target, sim.State.Units[0].Position);
        Assert.False(sim.State.Units[0].Moving);
    }

    [Fact]
    public void Bezier_positions_follow_arc_length_not_curve_parameter()
    {
        // Handles collapsed onto the endpoints: t is very non-uniform along the line.
        var segment = new BezierSegment(Vector3.Zero, Vector3.Zero, new(3, 0, 0), new(3, 0, 0));

        Assert.Equal(3f, segment.Length, 0.001f);
        Assert.Equal(1.5f, segment.PositionAt(1.5f).X, 0.01f);
        Assert.Equal(0.5f, segment.PositionAt(0.5f).X, 0.01f);
        Assert.Equal(Vector3.UnitX, segment.DirectionAt(0)); // zero derivative at the end is handled
    }

    [Fact]
    public void Packages_cross_segments_and_are_lost_at_the_end()
    {
        var sim = new Simulation();
        // Two 1.5 m segments, 2 m/s, one package per second.
        sim.AddBeltLine(
            [Straight(Vector3.Zero, new(1.5f, 0, 0)), Straight(new(1.5f, 0, 0), new(3, 0, 0))],
            speed: 2, spawnIntervalSeconds: 1);

        sim.Tick(NoCommands); // tick 1 spawns the first package at the start
        Assert.Single(sim.State.Packages);

        for (int i = 0; i < 20; i++) sim.Tick(NoCommands); // 0.1 m per tick: 2 m along, on the second segment
        var p = sim.State.Packages[0];
        Assert.Equal(1, p.Segment);
        Assert.Equal(2f, p.Position.X, 0.01f);

        int lost = 0;
        for (int i = 0; i < 19; i++) // through tick 40: the first package reaches 3 m at tick 31
            foreach (var e in sim.Tick(NoCommands))
                if (e.Kind == SimEventKind.PackageLost) lost++;

        Assert.Equal(1, lost);
        Assert.Single(sim.State.Packages); // the second one, spawned at tick 21
    }
}
