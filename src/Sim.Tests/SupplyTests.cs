using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Finite sources: a reserve that spawns, takes back what reaches the end, and drains only through posts and spills.</summary>
public class SupplyTests
{
    static BeltConfig Finite(int supply, float spillLoss = 0) => Belt(0.5f, spillLoss) with { Supply = supply };

    static int OnBelt(Simulation sim) => sim.State.Belts[0].Packages.Count;

    [Fact]
    public void What_nobody_takes_comes_back_round_so_the_reserve_never_runs_dry()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Finite(supply: 30));
        var line = sim.State.Belts[0];

        Run(sim, 60 * T); // far more spawns than 30 at 2/s, and the line is 20 m at 2 m/s
        Assert.True(line.Spawned > 30);
        Assert.True(line.Returned > 0);
        Assert.Equal(0, line.Lost);
        Assert.Equal(30, line.Reserve + OnBelt(sim));
    }

    [Fact]
    public void Posts_drain_it_and_it_goes_dry()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Finite(supply: 10));
        sim.AddGatherer(Blue, new Vector3(10, 0, 2), maxDistance: 3);
        var line = sim.State.Belts[0];

        Run(sim, 120 * T);
        Assert.Equal(10, sim.State.Players[Blue].Gathered);
        Assert.Equal(0, line.Reserve);
        Assert.Equal(0, OnBelt(sim));
        int spawned = line.Spawned;
        Run(sim, 5 * T);
        Assert.Equal(spawned, line.Spawned); // dry: nothing more
    }

    [Fact]
    public void A_break_wastes_what_falls_off_and_repair_stops_the_bleeding()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Finite(supply: 40, spillLoss: 1)); // every spill breaks in the fall
        var line = sim.State.Belts[0];

        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 10 * T);
        int left = line.Reserve + OnBelt(sim);
        Assert.Equal(40 - line.Destroyed, left);
        Assert.True(line.Destroyed > 0);

        sim.State.Players[Blue].Resources = 100;
        int builder = sim.AddUnit(Blue, new Vector3(15, 0, 2), speed: 5, repairSeconds: 1);
        sim.Tick([new RepairSegmentCommand(Blue, builder, 0, 1)]);
        Run(sim, 5 * T);
        int destroyed = line.Destroyed;
        Run(sim, 20 * T);
        Assert.Equal(destroyed, line.Destroyed); // mended: nothing more lost
        Assert.Equal(40 - destroyed, line.Reserve + OnBelt(sim));
    }

    [Fact]
    public void An_unlimited_source_still_loses_what_reaches_the_end()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(0.5f));
        Run(sim, 30 * T);
        Assert.True(sim.State.Belts[0].Lost > 0);
        Assert.Equal(0, sim.State.Belts[0].Returned);
    }
}
