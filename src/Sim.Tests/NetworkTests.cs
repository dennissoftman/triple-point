using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Gatherer posts and junctions: how packages leave the belt and move between lines.</summary>
public class NetworkTests
{
    const int T = Simulation.TicksPerSecond;

    [Fact]
    public void Gatherer_takes_one_package_per_work_cycle_and_lets_the_rest_pass()
    {
        var sim = new Simulation();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(0.5f)); // 2 packages/s
        sim.AddGatherer(new Vector3(10, 0, 2), maxDistance: 3);

        Run(sim, 60 * T);

        // One per 2 s over the ~55 s after the first package reaches 10 m.
        Assert.InRange(sim.State.Gathered, 26, 29);
        Assert.Equal(sim.State.Gathered, sim.State.Resources);
        Assert.True(sim.State.Belts[0].Lost > sim.State.Gathered); // most of 2/s passes a 0.5/s post
    }

    [Fact]
    public void Upstream_gatherer_starves_a_downstream_one_when_flow_matches_its_rate()
    {
        var sim = new Simulation();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(Simulation.GatherSeconds)); // 1 per work cycle
        sim.AddGatherer(new Vector3(5, 0, 2), maxDistance: 3);
        sim.AddGatherer(new Vector3(15, 0, 2), maxDistance: 3);

        Run(sim, 60 * T);

        Assert.True(sim.State.Gatherers[0].Gathered > 20);
        Assert.Equal(0, sim.State.Gatherers[1].Gathered);
        Assert.Equal(0, sim.State.Belts[0].Lost);
    }

    // Two 10 m inputs meeting at (10, 0, 0), then one output to (20, 0, 0).
    static Simulation Merge(float spawnIntervalSeconds)
    {
        var sim = new Simulation();
        sim.AddBeltLine([Straight(new(0, 0, -5), new(10, 0, 0))], Belt(spawnIntervalSeconds));
        sim.AddBeltLine([Straight(new(0, 0, 5), new(10, 0, 0))], Belt(spawnIntervalSeconds));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, 0))], Belt(spawnIntervalSeconds));
        sim.AddJunction(new Vector3(10, 0, 0), attachRadius: 0.5f);
        return sim;
    }

    [Fact]
    public void Junction_attaches_lines_by_endpoints_and_fed_lines_do_not_spawn()
    {
        var sim = Merge(0.5f);
        var junction = sim.State.Junctions[0];

        Assert.Equal([0, 1], junction.Inputs);
        Assert.Equal([2], junction.Outputs);
        Run(sim, 20 * T);
        Assert.Equal(0, sim.State.Belts[2].Spawned);
        Assert.True(sim.State.Belts[2].Lost > 0); // packages arrive through the junction
        Assert.Equal(0, sim.State.Belts[0].Lost);  // an input hands off instead of losing
    }

    [Fact]
    public void Overloaded_merge_takes_turns_keeps_spacing_and_backs_up_both_inputs()
    {
        // Each input brings 2/s; the output carries at most 2/s (2 m/s, 1 m spacing).
        var sim = Merge(0.5f);
        var (a, b, output) = (sim.State.Belts[0], sim.State.Belts[1], sim.State.Belts[2]);

        Run(sim, 60 * T);

        Assert.True(a.BlockedSpawns > 0 && b.BlockedSpawns > 0);
        Assert.InRange(a.Spawned - b.Spawned, -2, 2); // neither input is starved
        for (int i = 1; i < output.Packages.Count; i++)
            Assert.True(output.Packages[i - 1].Distance - output.Packages[i].Distance >= 1 - 2e-3f);
    }

    [Fact]
    public void Switch_feeds_only_the_selected_output_and_a_unit_flips_it()
    {
        var sim = new Simulation();
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, -5))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, 5))], Belt(1));
        int j = sim.AddJunction(new Vector3(10, 0, 0), attachRadius: 0.5f);
        var (left, right) = (sim.State.Belts[1], sim.State.Belts[2]);
        int unit = sim.AddUnit(new Vector3(10, 0, 8), speed: 5); // 8 m away: walks 5.5 m to get in range

        Run(sim, 20 * T);
        Assert.True(left.Lost > 0);
        Assert.Equal(0, right.Lost + right.Packages.Count);

        var events = sim.Tick([new SwitchJunctionCommand(unit, j, Output: 1)]);
        int switchedAt = -1;
        for (int tick = 1; tick <= 100 && switchedAt < 0; tick++)
        {
            if (events.Contains(new SimEvent(SimEventKind.JunctionSwitched, j, 1))) switchedAt = tick;
            else events = sim.Tick(NoCommands);
        }
        Assert.InRange(switchedAt, 22, 24); // 5.5 m at 5 m/s
        Assert.Equal(1, sim.State.Junctions[j].Selected);

        var (leftLost, leftCarrying) = (left.Lost, left.Packages.Count);
        Run(sim, 20 * T);
        Assert.True(right.Lost > 0);
        Assert.Equal(leftCarrying, left.Lost - leftLost); // only what was already on it
        Assert.Empty(left.Packages);
    }
}
