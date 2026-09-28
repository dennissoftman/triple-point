using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Gatherer posts and junctions: how packages leave the belt and move between lines.</summary>
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

    // Two 10 m inputs meeting at (10, 0, 0), then one output to (20, 0, 0).
    static Simulation Merge(float spawnIntervalSeconds)
    {
        var sim = NewSim();
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

    // One input into a switch at (10, 0, 0), with a left output (toward -z) and a right one (+z).
    static (Simulation sim, int junction) Switch()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, -5))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, 5))], Belt(1));
        return (sim, sim.AddJunction(new Vector3(10, 0, 0), attachRadius: 0.5f));
    }

    [Fact]
    public void Neutral_switch_splits_the_stream_between_its_outputs()
    {
        var (sim, j) = Switch();
        var (input, left, right) = (sim.State.Belts[0], sim.State.Belts[1], sim.State.Belts[2]);

        Run(sim, 40 * T);

        Assert.Equal(-1, sim.State.Junctions[j].Selected);
        Assert.Equal(Player.None, sim.State.Junctions[j].Owner);
        Assert.True(left.Lost > 5 && right.Lost > 5, $"left {left.Lost}, right {right.Lost}");
        Assert.InRange(left.Lost - right.Lost, -1, 1); // dealt out in turn
        Assert.Equal(0, input.BlockedSpawns);           // it never holds the stream up
    }

    [Fact]
    public void Capturing_a_switch_turns_it_to_the_captors_side_and_only_the_owner_flips_it()
    {
        var (sim, j) = Switch();
        var junction = sim.State.Junctions[j];
        var (left, right) = (sim.State.Belts[1], sim.State.Belts[2]);
        sim.AddGatherer(Red, new Vector3(17, 0, -6), maxDistance: 3); // on the left output
        sim.AddGatherer(Blue, new Vector3(17, 0, 6), maxDistance: 3); // on the right one
        int blue = sim.AddUnit(Blue, new Vector3(10, 0, 8), speed: 5, dps: 0);

        sim.Tick([new SetJunctionCommand(Blue, j, 0)]);
        Assert.Equal(-1, junction.Selected); // not Blue's yet: still splitting

        // 4 m to walk into the 4 m capture radius (16 ticks), then 5 s uncontested (100 ticks). On the tick
        // it's taken it turns toward Blue's post.
        var captured = new SimEvent(SimEventKind.JunctionCaptured, j, Blue);
        var events = sim.Tick([new MoveCommand(Blue, blue, new Vector3(10, 0, 0))]);
        int ticks = 1;
        for (; !events.Contains(captured) && ticks < 200; ticks++) events = sim.Tick(NoCommands);
        Assert.InRange(ticks, 115, 118);
        Assert.Contains(new SimEvent(SimEventKind.JunctionSwitched, j, 1), events);
        Assert.Equal((Blue, 1), (junction.Owner, junction.Selected));

        sim.Tick([new SetJunctionCommand(Red, j, 0)]);
        Assert.Equal(1, junction.Selected); // Red doesn't own it
        sim.Tick([new SetJunctionCommand(Blue, j, 0)]);
        Assert.Equal(0, junction.Selected); // the owner flips it, from anywhere
        Run(sim, 10 * T);
        Assert.Empty(right.Packages); // all of it goes left now
        Assert.NotEmpty(left.Packages);
        sim.Tick([new SetJunctionCommand(Blue, j, 1)]);

        // Blue walks off and Red takes it: it turns toward Red's post, on the left.
        int red = sim.AddUnit(Red, new Vector3(10, 0, -8), speed: 5, dps: 0);
        sim.Tick([new MoveCommand(Blue, blue, new Vector3(10, 0, 20))]);
        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.JunctionCaptured, j, Red), 300,
            [new MoveCommand(Red, red, new Vector3(10, 0, 0))]) > 0);
        Assert.Equal((Red, 0), (junction.Owner, junction.Selected));
    }

    [Fact]
    public void Captured_switch_with_none_of_the_captors_posts_downstream_still_feeds_a_side()
    {
        var (sim, j) = Switch();
        sim.AddUnit(Blue, new Vector3(10, 0, 1), dps: 0);

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.JunctionCaptured, j, Blue), 150) > 0);
        Assert.Equal(0, sim.State.Junctions[j].Selected);
    }

    [Fact]
    public void Capture_looks_past_other_junctions_for_the_captors_posts()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, -5))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, 5))], Belt(1));
        sim.AddBeltLine([Straight(new(20, 0, 5), new(30, 0, 5))], Belt(1)); // past a junction at the right output's end
        int j = sim.AddJunction(new Vector3(10, 0, 0), attachRadius: 0.5f);
        sim.AddJunction(new Vector3(20, 0, 5), attachRadius: 0.5f);
        sim.AddGatherer(Blue, new Vector3(25, 0, 7), maxDistance: 3);
        sim.AddUnit(Blue, new Vector3(10, 0, 1), dps: 0);

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.JunctionCaptured, j, Blue), 150) > 0);
        Assert.Equal(1, sim.State.Junctions[j].Selected);
    }

    [Fact]
    public void Contested_switch_does_not_change_hands()
    {
        var (sim, j) = Switch();
        // Both at the switch; unarmed, so neither side wins the spot.
        sim.AddUnit(Blue, new Vector3(10, 0, 1), dps: 0);
        sim.AddUnit(Red, new Vector3(10, 0, -1), dps: 0);

        Run(sim, 20 * T);

        Assert.Equal(Player.None, sim.State.Junctions[j].Owner);
        Assert.Equal(0f, sim.State.Junctions[j].CaptureProgress);
    }

    [Fact]
    public void Vehicles_cannot_capture_a_switch_but_deny_it_to_the_enemy()
    {
        var (sim, j) = Switch();
        var junction = sim.State.Junctions[j];
        sim.AddUnit(Blue, new Vector3(10, 0, 1), dps: 0, movement: Movement.Tracked, acceleration: 3, turnRate: 60, canCapture: false);

        Run(sim, 10 * T);
        Assert.Equal(Player.None, junction.Owner); // a vehicle alone takes nothing
        Assert.Equal(0f, junction.CaptureProgress);

        sim.AddUnit(Red, new Vector3(10, 0, -1), dps: 0); // a squad that could capture...
        Run(sim, 10 * T);
        Assert.Equal(Player.None, junction.Owner);  // ...but Blue's vehicle is there
        Assert.Equal(0f, junction.CaptureProgress);
    }

    [Fact]
    public void Capture_progress_drains_when_the_capturer_leaves()
    {
        var (sim, j) = Switch();
        var junction = sim.State.Junctions[j];
        int unit = sim.AddUnit(Blue, new Vector3(10, 0, 1));

        Run(sim, 3 * T);
        Assert.Equal(0.6f, junction.CaptureProgress, 0.02f);
        Assert.Equal(Blue, junction.Capturer);

        sim.Tick([new MoveCommand(Blue, unit, new Vector3(10, 0, 20))]);
        Run(sim, 5 * T);
        Assert.Equal(0f, junction.CaptureProgress);
        Assert.Equal(Player.None, junction.Owner);
    }
}
