using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Gatherer posts: how packages leave the belt.</summary>
public class NetworkTests
{
    [Fact]
    public void Gatherer_takes_one_package_per_work_cycle_and_lets_the_rest_pass()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(0.5f)); // 2 packages/s
        sim.AddGatherer(Blue, new Vector3(10, 0, 2), maxDistance: 3);

        Run(sim, 60 * T);

        // One per 2 s over the ~55 s after the first package reaches 10 m.
        Assert.InRange(sim.State.Players[Blue].Gathered, 26, 29);
        Assert.Equal(sim.State.Players[Blue].Gathered, sim.State.Players[Blue].Packages);
        Assert.True(sim.State.Belts[0].Lost > sim.State.Players[Blue].Gathered); // most of 2/s passes a 0.5/s post
    }

    [Fact]
    public void Upstream_gatherer_starves_a_downstream_one_when_flow_matches_its_rate()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(Simulation.GatherSeconds)); // 1 per work cycle
        sim.AddGatherer(Blue, new Vector3(5, 0, 2), maxDistance: 3);
        sim.AddGatherer(Blue, new Vector3(15, 0, 2), maxDistance: 3);

        Run(sim, 60 * T);

        Assert.True(sim.State.Gatherers[0].Gathered > 20);
        Assert.Equal(0, sim.State.Gatherers[1].Gathered);
        Assert.Equal(0, sim.State.Belts[0].Lost);
    }

    [Fact]
    public void Covered_segments_can_not_be_attacked_broken_or_built_beside()
    {
        var sim = NewSim();
        // Segments 0-10 m (covered), 10-30 m (open), 30-40 m (covered).
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0)), Straight(new(10, 0, 0), new(30, 0, 0)), Straight(new(30, 0, 0), new(40, 0, 0))],
            Belt(1), coveredStart: 10, coveredEnd: 10);
        var segments = sim.State.Belts[0].Segments;
        Assert.Equal([true, false, true], segments.Select(s => s.Covered));

        int blue = sim.AddUnit(Blue, new Vector3(5, 0, 3), dps: 100, range: 8);
        sim.Tick([new AttackSegmentCommand(Blue, blue, 0, 0), new BreakSegmentCommand(0, 2)]);
        Run(sim, 2 * T);
        Assert.All(segments, s => Assert.Equal(s.MaxHealth, s.Health));
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind); // the order was never taken

        // Posts snap past the covered part, to the open belt.
        Assert.True(sim.SnapToBelt(new Vector3(6, 0, 2), 6, out var at, out _));
        Assert.Equal(10f, at.X, 0.01f);
        Assert.False(sim.FindSegment(new Vector3(4, 0, 1), 1.5f, out _, out _, openOnly: true));
    }

    [Fact]
    public void Idle_builders_repair_damaged_belt_nearby_by_themselves_while_their_owner_can_pay()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1));
        var segments = sim.State.Belts[0].Segments;
        int near = sim.AddUnit(Blue, new Vector3(15, 0, 8), speed: 5, builds: [], repairSeconds: 5, repairCost: 4);  // 8 m from segment 1
        int far = sim.AddUnit(Red, new Vector3(5, 0, 30), speed: 5, builds: [], repairSeconds: 5, repairCost: 4);    // 30 m from both
        sim.State.Players[Red].Packages = 10;

        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 20);
        Assert.Equal(UnitOrder.None, UnitById(sim, near).Current.Kind); // Blue is broke: it stays put

        sim.State.Players[Blue].Packages = 2; // half a repair
        Run(sim, 20);
        Assert.Equal(UnitOrder.Repair, UnitById(sim, near).Current.Kind);
        Run(sim, 200);
        Assert.Equal(0, sim.State.Players[Blue].Packages);
        Assert.Equal(segments[1].MaxHealth / 2, segments[1].Health, 1f); // stalled halfway when the money ran out
        Assert.Equal(SegmentState.Broken, segments[1].State);
        Assert.Equal(UnitOrder.Repair, UnitById(sim, near).Current.Kind); // still on it, waiting for money

        sim.State.Players[Blue].Packages = 5;
        Run(sim, 60);
        Assert.Equal(SegmentState.Normal, segments[1].State);
        Assert.Equal(new Vector3(5, 0, 30), UnitById(sim, far).Position); // out of reach: never went
    }
}
