using System.Numerics;

namespace Sim.Tests;

public class SimulationTests
{
    static readonly Command[] NoCommands = [];

    // Evenly spaced control points on a line: t advances uniformly with distance.
    static BezierSegment Straight(Vector3 from, Vector3 to) =>
        new(from, Vector3.Lerp(from, to, 1 / 3f), Vector3.Lerp(from, to, 2 / 3f), to);

    static void Run(Simulation sim, int ticks)
    {
        for (int i = 0; i < ticks; i++) sim.Tick(NoCommands);
    }

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
        Assert.Equal(UnitOrder.None, sim.State.Units[0].Order);
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
            speed: 2, spacing: 1, spawnIntervalSeconds: 1);
        var line = sim.State.Belts[0];

        sim.Tick(NoCommands); // tick 1 spawns the first package at the start
        Assert.Single(line.Packages);

        Run(sim, 20); // 0.1 m per tick: 2 m along, on the second segment
        Assert.Equal(1, line.Packages[0].Segment);
        Assert.Equal(2f, line.Packages[0].Position.X, 0.01f);

        Run(sim, 19); // through tick 40: the first package reaches 3 m at tick 31
        Assert.Equal(1, line.Lost);
        Assert.Single(line.Packages); // the second one, spawned at tick 21
    }

    [Fact]
    public void Broken_segment_holds_packages_and_the_queue_blocks_the_source()
    {
        var sim = new Simulation();
        sim.AddBeltLine(
            [Straight(Vector3.Zero, new(10, 0, 0)), Straight(new(10, 0, 0), new(20, 0, 0))],
            speed: 2, spacing: 1, spawnIntervalSeconds: 0.25f);
        var line = sim.State.Belts[0];

        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 400);

        Assert.Equal(SegmentState.Broken, line.Segments[1].State);
        Assert.Equal(0, line.Lost);
        // Queued nose to tail from the broken segment's start back to the source.
        Assert.Equal(10, line.Packages.Count);
        Assert.InRange(line.Packages[0].Distance, 9.99f, 10f);
        for (int i = 1; i < line.Packages.Count; i++)
            Assert.True(line.Packages[i - 1].Distance - line.Packages[i].Distance >= 1 - 1e-4f);
        Assert.True(line.BlockedSpawns > 0);
    }

    [Fact]
    public void Unit_walks_to_broken_segment_repairs_it_and_flow_resumes()
    {
        var sim = new Simulation();
        sim.AddBeltLine(
            [Straight(Vector3.Zero, new(10, 0, 0)), Straight(new(10, 0, 0), new(20, 0, 0))],
            speed: 2, spacing: 1, spawnIntervalSeconds: 1);
        var line = sim.State.Belts[0];
        int unit = sim.AddUnit(new Vector3(10, 0, 5), speed: 5); // 5 m from the segment start

        var events = sim.Tick([new BreakSegmentCommand(0, 1), new RepairSegmentCommand(unit, 0, 1)]);
        int repairedAt = -1;
        for (int tick = 1; tick <= 200 && repairedAt < 0; tick++)
        {
            if (events.Contains(new SimEvent(SimEventKind.SegmentRepaired, 0, 1))) repairedAt = tick;
            else events = sim.Tick(NoCommands);
        }

        // 2.5 m to get in range (10 ticks), then 5 s of work (100 ticks).
        Assert.InRange(repairedAt, 110, 112);
        Assert.Equal(SegmentState.Normal, line.Segments[1].State);
        Assert.Equal(UnitOrder.None, sim.State.Units[0].Order);
        Assert.Equal(0, line.Lost);

        Run(sim, 200);
        Assert.True(line.Lost > 0);
    }
}
