using System.Numerics;
using System.Runtime.InteropServices;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

public class VisionTests
{
    static readonly BuildingType Hut = new(Health: 100, Size: 4, Sight: 6, Id: "hut");

    static Simulation FogSim()
    {
        var sim = NewSim();
        sim.EnableNavigation(-100, -100, 100, 100);
        sim.FogOfWar = true;
        return sim;
    }

    static int Unit(Simulation sim, int owner, Vector3 at, float sight = 20, float dps = 10, float range = 8, float speed = 5)
    {
        int id = sim.AddUnit(owner, at, speed: speed, dps: dps, range: range);
        CollectionsMarshal.AsSpan(sim.State.Units)[^1].Sight = sight;
        return id;
    }

    [Fact]
    public void A_side_sees_round_its_units_and_nowhere_else()
    {
        var sim = FogSim();
        Unit(sim, Blue, Vector3.Zero, sight: 20);
        int near = Unit(sim, Red, new(15, 0, 0), dps: 0), far = Unit(sim, Red, new(40, 0, 0), dps: 0);
        Run(sim, 1);

        Assert.True(sim.Sees(Blue, new(15, 0, 0)));
        Assert.False(sim.Sees(Blue, new(40, 0, 0)));
        Assert.True(sim.SeesUnit(Blue, UnitById(sim, near)));
        Assert.False(sim.SeesUnit(Blue, UnitById(sim, far)));
        Assert.True(sim.SeesUnit(Red, UnitById(sim, far))); // its own, always

        sim.FogOfWar = false;
        Assert.True(sim.SeesUnit(Blue, UnitById(sim, far)));
    }

    [Fact]
    public void Units_only_shoot_what_their_side_sees_so_long_guns_need_a_spotter()
    {
        var sim = FogSim();
        int gun = Unit(sim, Blue, Vector3.Zero, sight: 10, range: 25);
        int target = Unit(sim, Red, new(20, 0, 0), dps: 0, sight: 5);
        Run(sim, 2 * T);
        Assert.Equal(100f, UnitById(sim, target).Health); // in range, out of sight

        Unit(sim, Blue, new(12, 0, 0), sight: 10, dps: 0); // a spotter
        Run(sim, 2 * T);
        Assert.True(UnitById(sim, target).Health < 100f);
        Assert.True(UnitById(sim, gun).Firing);
    }

    [Fact]
    public void A_shot_gives_the_shooter_away_to_the_side_it_hit_for_a_while()
    {
        var sim = FogSim();
        int gun = Unit(sim, Blue, Vector3.Zero, sight: 10, range: 25);
        Unit(sim, Blue, new(12, 0, 0), sight: 10, dps: 0); // spotter
        Unit(sim, Red, new(20, 0, 0), dps: 0, sight: 5);
        Run(sim, 3);
        Assert.True(sim.SeesUnit(Red, UnitById(sim, gun)), "Red sees who's shooting it");

        sim.Tick([new MoveCommand(Blue, gun, new(-60, 0, 0))]); // drives off, firing on the move for a second
        Run(sim, 5 * T);                                         // until out of range, then 3 s more
        Assert.False(sim.SeesUnit(Red, UnitById(sim, gun)));
    }

    [Fact]
    public void Enemy_buildings_are_remembered_as_last_seen()
    {
        var sim = FogSim();
        int start = sim.AddBuilding(Red, new(60, 0, 0), Hut); // there from the start: known
        int scout = Unit(sim, Blue, Vector3.Zero, sight: 20);
        Run(sim, 1);
        Assert.Contains(sim.Vision(Blue).Ghosts, g => g.Id == start);

        int later = sim.AddBuilding(Red, new(0, 0, 60), Hut); // put up out of sight: unknown
        Run(sim, 1);
        Assert.DoesNotContain(sim.Vision(Blue).Ghosts, g => g.Id == later);

        sim.Tick([new MoveCommand(Blue, scout, new(0, 0, 45))]);
        Run(sim, 12 * T);
        Assert.Contains(sim.Vision(Blue).Ghosts, g => g.Id == later); // seen now

        sim.Tick([new MoveCommand(Blue, scout, Vector3.Zero)]);
        Run(sim, 12 * T);
        sim.Tick([new DestroyCommand(later)]);
        Run(sim, 2);
        Assert.Contains(sim.Vision(Blue).Ghosts, g => g.Id == later); // gone, but it hasn't seen that

        sim.Tick([new MoveCommand(Blue, scout, new(0, 0, 45))]);
        Run(sim, 12 * T);
        Assert.DoesNotContain(sim.Vision(Blue).Ghosts, g => g.Id == later); // it looked again
    }

    [Fact]
    public void Belt_is_remembered_as_last_seen_except_breaks_that_cut_off_your_posts()
    {
        var sim = FogSim();
        sim.AddBeltLine([TestHelpers.Straight(new(-80, 0, 0), new(80, 0, 0))], new BeltConfig(2, 1, 1, MaxSegmentLength: 5));
        Unit(sim, Blue, new(0, 0, -50), sight: 10); // sees none of the belt
        Run(sim, 1);
        int far = 2; // 10-15 m along, near the start, upstream of everything

        sim.Tick([new BreakSegmentCommand(0, far)]);
        Run(sim, 1);
        Assert.Equal(SegmentState.Broken, sim.State.Belts[0].Segments[far].State);
        Assert.Equal(SegmentState.Normal, sim.SeenState(Blue, 0, far));

        // A post downstream: a break upstream of it is news at once.
        Assert.True(sim.AddGatherer(Blue, new(60, 0, 2), 4) >= 0);
        int upstream = 5;
        sim.Tick([new BreakSegmentCommand(0, upstream)]);
        Assert.True(sim.FeedsPostOf(Blue, 0, upstream));
        Assert.Equal(SegmentState.Broken, sim.SeenState(Blue, 0, upstream));
        Assert.Equal(SegmentState.Normal, sim.SeenState(Red, 0, upstream));
    }

    [Fact]
    public void Nobody_can_be_ordered_to_attack_what_their_side_has_never_seen()
    {
        var sim = FogSim();
        int blue = Unit(sim, Blue, Vector3.Zero, sight: 10);
        int red = Unit(sim, Red, new(50, 0, 0), dps: 0);
        Run(sim, 1);
        sim.Tick([new AttackCommand(Blue, blue, red)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind);
    }

    [Fact]
    public void An_attack_on_a_unit_that_slips_out_of_sight_goes_to_where_it_was_seen_and_stops()
    {
        var sim = FogSim();
        int blue = Unit(sim, Blue, Vector3.Zero, sight: 12, speed: 3, range: 4);
        int red = Unit(sim, Red, new(10, 0, 0), dps: 0, speed: 8);
        Run(sim, 1);
        sim.Tick([new AttackCommand(Blue, blue, red), new MoveCommand(Red, red, new(80, 0, 0))]);
        Run(sim, 15 * T);
        var u = UnitById(sim, blue);
        Assert.Equal(UnitOrder.None, u.Current.Kind);
        Assert.InRange(u.Position.X, 5, 30); // went after it a way, not across the map
    }
}
