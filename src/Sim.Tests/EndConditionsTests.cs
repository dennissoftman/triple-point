using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>
/// Losing and winning (CanStillRecover): the doctrine's edge-case table, for what the game has so far.
/// Still to come with their features: builders in transports, captured civilian buildings, team games,
/// the Western rebuild fallback, and refunds.
/// </summary>
public class EndConditionsTests
{
    static readonly WeaponType Gun = new(WeaponKind.Bullet, Damage: 1, Reload: 0.1f, Range: 10);
    static readonly UnitType Gunner = new(Members: 1, Speed: 0, MemberHealth: 200, Weapon: "gun", Movement: Movement.Static, Id: "gunner") { Gun = Gun };
    static readonly UnitType Builder = new(Members: 1, Speed: 5, MemberHealth: 100, Builds: ["barracks", "post", "gunner"], Id: "builder") { Gun = WeaponType.Unarmed };
    static readonly BuildingType Hq = new(Health: 1000, Size: 4, Id: "hq");
    static readonly BuildingType Barracks = new(Health: 500, Size: 4, Cost: 10, BuildTime: 100, Id: "barracks");
    static readonly BuildingType Post = new(Health: 300, Size: 2, Cost: 5, BuildTime: 1, Kind: BuildingKind.Post, Id: "post");
    static readonly BuildingType GunnerNest = new(Health: 400, Size: 2, Cost: 2, BuildTime: 1, Kind: BuildingKind.Defense, Unit: "gunner", Id: "gunner") { Defense = Gunner };
    static readonly int Grace = (int)(Simulation.GraceSeconds * T);

    // Two players with end conditions on; Red keeps an HQ far away, so only Blue's fate is in question.
    static Simulation NewGame(int bluePackages = 0)
    {
        var sim = NewSim();
        sim.EndConditions = true;
        sim.BuildingTypes = new Dictionary<string, BuildingType> { ["hq"] = Hq, ["barracks"] = Barracks, ["post"] = Post, ["gunner"] = GunnerNest };
        sim.State.Players[Blue].Packages = bluePackages;
        sim.AddBuilding(Red, new Vector3(100, 0, 100), Hq);
        return sim;
    }

    static Player BluePlayer(Simulation sim) => sim.State.Players[Blue];

    [Fact]
    public void A_player_with_a_building_is_safe()
    {
        var sim = NewGame();
        sim.AddBuilding(Blue, new Vector3(-100, 0, -100), Hq);

        Run(sim, 5 * T);

        Assert.True(sim.CanStillRecover(Blue));
        Assert.Equal(-1, BluePlayer(sim).GraceTicksLeft);
        Assert.False(sim.State.GameOver);
    }

    [Fact]
    public void No_buildings_and_no_builder_loses_at_once_and_the_other_side_wins()
    {
        var sim = NewGame(bluePackages: 100);
        sim.AddUnit(Blue, Vector3.Zero, dps: 0); // a unit, but not one that can build

        var events = sim.Tick(NoCommands);

        Assert.Contains(new SimEvent(SimEventKind.PlayerLost, Blue), events);
        Assert.Contains(new SimEvent(SimEventKind.GameOver, Red), events);
        Assert.True(BluePlayer(sim).Lost);
        Assert.Equal((true, Red), (sim.State.GameOver, sim.State.Winner));
    }

    [Fact]
    public void A_builder_without_the_money_for_the_cheapest_building_loses_at_once()
    {
        // 3 would pay for the defense (2), but defenses don't count: the cheapest real building is the post (5).
        var sim = NewGame(bluePackages: 3);
        sim.AddUnit(Blue, Vector3.Zero, Builder);

        sim.Tick(NoCommands);

        Assert.True(BluePlayer(sim).Lost);
    }

    [Fact]
    public void A_builder_with_the_money_gets_the_grace_time_then_loses()
    {
        var sim = NewGame(bluePackages: 5);
        sim.AddUnit(Blue, Vector3.Zero, Builder);

        var events = sim.Tick(NoCommands);
        Assert.Contains(new SimEvent(SimEventKind.GraceStarted, Blue), events);
        Run(sim, Grace - 2);
        Assert.False(BluePlayer(sim).Lost);
        Assert.Equal(1, BluePlayer(sim).GraceTicksLeft);

        sim.Tick(NoCommands);
        Assert.True(BluePlayer(sim).Lost);
    }

    [Fact]
    public void The_clock_pauses_only_while_a_builder_works_on_a_foundation()
    {
        var sim = NewGame(bluePackages: 20);
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);

        sim.Tick([new BuildCommand(Blue, builder, "barracks", new Vector3(4, 0, 0))]); // within reach: laid at once
        Run(sim, 5 * T);
        Assert.True(BluePlayer(sim).GracePaused);
        Assert.Equal(Grace, BluePlayer(sim).GraceTicksLeft); // started, then paused from the first tick

        sim.Tick([new MoveCommand(Blue, builder, new Vector3(-50, 0, 0))]); // abandon it
        Run(sim, 10 * T);
        Assert.False(BluePlayer(sim).GracePaused);
        Assert.InRange(BluePlayer(sim).GraceTicksLeft, Grace - 10 * T - 2, Grace - 10 * T + 2); // counting again, not reset
    }

    [Fact]
    public void Finishing_a_post_stops_the_clock_and_posts_keep_a_player_in_the_game()
    {
        var sim = NewGame(bluePackages: 5);
        sim.AddBeltLine(TwoSegments(), Belt(1));
        int builder = sim.AddUnit(Blue, new Vector3(10, 0, 4), Builder);

        sim.Tick([new BuildCommand(Blue, builder, "post", new Vector3(10, 0, 2))]);
        Run(sim, 2 * T);
        Assert.Single(sim.State.Gatherers);
        Assert.Equal(-1, BluePlayer(sim).GraceTicksLeft);

        sim.Tick([new DestroyCommand(builder)]); // no builder left, but the post still stands
        Run(sim, Grace + T);
        Assert.False(BluePlayer(sim).Lost);
    }

    [Fact]
    public void A_lone_defense_does_not_keep_a_player_in_the_game()
    {
        var sim = NewGame();
        sim.AddUnit(Blue, Vector3.Zero, Gunner);

        sim.Tick(NoCommands);

        Assert.True(BluePlayer(sim).Lost);
    }

    [Fact]
    public void Losing_destroys_everything_the_player_still_has()
    {
        var sim = NewGame(bluePackages: 20);
        sim.AddBeltLine(TwoSegments(), Belt(1));
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);
        sim.AddUnit(Blue, new Vector3(0, 0, 10), Gunner);
        sim.AddGatherer(Blue, new Vector3(10, 0, 2), 4);
        sim.AddBuilding(Blue, new Vector3(-20, 0, 0), Barracks, built: false);

        sim.Tick([new DestroyCommand(sim.State.Gatherers[0].Id)]); // its only building
        sim.Tick([new DestroyCommand(builder)]);                   // and its only builder: no way back
        Run(sim, 2);

        Assert.True(BluePlayer(sim).Lost);
        Assert.DoesNotContain(sim.State.Units, u => u.Owner == Blue);
        Assert.DoesNotContain(sim.State.Gatherers, g => g.Owner == Blue);
        Assert.DoesNotContain(sim.State.Buildings, b => b.Owner == Blue);
    }

    [Fact]
    public void Both_sides_losing_on_the_same_tick_is_a_draw()
    {
        var sim = NewSim();
        sim.EndConditions = true;

        var events = sim.Tick(NoCommands);

        Assert.Contains(new SimEvent(SimEventKind.GameOver, Player.None), events);
        Assert.Equal((true, Player.None), (sim.State.GameOver, sim.State.Winner));
    }

    [Fact]
    public void Nothing_ends_while_end_conditions_are_off()
    {
        var sim = NewSim(); // no buildings, no units: would be a draw at once with them on

        Run(sim, T);

        Assert.False(sim.State.GameOver);
        Assert.DoesNotContain(sim.State.Players, p => p.Lost);
    }
}
