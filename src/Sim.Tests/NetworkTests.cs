using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Depots (gatherer posts in the code): how packages come off the trucks.</summary>
public class NetworkTests
{
    [Fact]
    public void A_depot_takes_a_third_of_each_passing_truck_stopping_it_a_moment()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(40, 0, 0))], Belt(1000) with { Load = 9 }); // one truck, 2 m/s
        sim.AddGatherer(Blue, new Vector3(10, 0, 2), maxDistance: 3);
        var line = sim.State.Belts[0];

        Run(sim, 5 * T + 2); // it reaches 10 m after 5 s
        var truck = line.Packages.Single();
        Assert.Equal(6, truck.Cargo);
        Assert.Equal(3, sim.State.Players[Blue].Gathered);
        Assert.Equal(3, sim.State.Players[Blue].Packages);
        float at = truck.Distance;
        Run(sim, (int)(Simulation.UnloadSeconds * T) - 10);
        Assert.Equal(at, line.Packages.Single().Distance); // still unloading
        Run(sim, 2 * T);
        Assert.True(line.Packages.Single().Distance > at + 1); // on its way
        Assert.Equal(3, sim.State.Players[Blue].Gathered);    // once per depot
    }

    [Fact]
    public void Three_depots_empty_a_truck_and_what_two_leave_goes_on()
    {
        int Lost(int depots)
        {
            var sim = NewSim();
            sim.AddBeltLine([Straight(Vector3.Zero, new(100, 0, 0))], Belt(1000) with { Load = 9 }); // one truck
            foreach (float x in new[] { 10f, 45f, 80f }.Take(depots)) sim.AddGatherer(Blue, new Vector3(x, 0, 2), maxDistance: 3);
            Run(sim, 70 * T);
            Assert.Empty(sim.State.Belts[0].Packages); // it got to the end
            Assert.All(sim.State.Gatherers, g => Assert.Equal(3, g.Gathered)); // a third each
            return sim.State.Belts[0].Lost;
        }
        Assert.Equal(0, Lost(3));
        Assert.Equal(3, Lost(2)); // two depots let a third through
        Assert.Equal(9, Lost(0));
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

        // Trucks there can't be shot at either.
        var events = sim.Tick([new AttackCommand(Blue, blue, sim.State.Belts[0].Packages[0].Id)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind);

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
