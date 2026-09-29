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
        Assert.Equal(sim.State.Players[Blue].Gathered, sim.State.Players[Blue].Resources);
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
}
