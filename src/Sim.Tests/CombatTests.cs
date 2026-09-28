using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Ownership, fighting, and squads.</summary>
public class CombatTests
{
    [Fact]
    public void Orders_to_another_players_units_are_ignored()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero);

        sim.Tick([new MoveCommand(Red, blue, new Vector3(10, 0, 0))]);
        Run(sim, 20);

        Assert.Equal(Vector3.Zero, UnitById(sim, blue).Position);
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind);
    }

    [Fact]
    public void Attack_chases_into_range_and_destroys_the_target()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(20, 0, 0), dps: 0); // unarmed, so it can't fight back

        // 12 m to get within 8 m (48 ticks), then 100 health at 10 dps (200 ticks).
        int diedAt = TicksUntil(sim, new SimEvent(SimEventKind.UnitDied, red), 400, [new AttackCommand(Blue, blue, red)]);

        Assert.InRange(diedAt, 247, 251);
        Assert.DoesNotContain(sim.State.Units, u => u.Id == red);
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind);
        Assert.Equal(12f, UnitById(sim, blue).Position.X, 0.3f); // fired from range, didn't walk up to it
    }

    [Fact]
    public void Units_cannot_be_ordered_to_attack_their_own_side()
    {
        var sim = NewSim();
        int a = sim.AddUnit(Blue, Vector3.Zero);
        int b = sim.AddUnit(Blue, new Vector3(3, 0, 0));

        sim.Tick([new AttackCommand(Blue, a, b)]);
        Run(sim, 20);

        Assert.Equal(100f, UnitById(sim, b).Health);
        Assert.Equal(UnitOrder.None, UnitById(sim, a).Current.Kind);
    }

    [Fact]
    public void Idle_units_fire_at_enemies_in_range_but_moving_units_do_not()
    {
        var sim = NewSim();
        int red = sim.AddUnit(Red, Vector3.Zero, dps: 10, range: 8);
        int blue = sim.AddUnit(Blue, new Vector3(-10, 0, 5), speed: 5, dps: 10, range: 8);

        // Blue walks past Red, 5 m away at the closest; it's inside Red's 8 m range for ~12.5 m (2.5 s).
        sim.Tick([new MoveCommand(Blue, blue, new Vector3(10, 0, 5))]);
        Run(sim, 6 * T);

        Assert.InRange(UnitById(sim, blue).Health, 72f, 78f);
        Assert.Equal(100f, UnitById(sim, red).Health); // Blue was moving: it didn't shoot back
        Assert.False(UnitById(sim, red).Firing);       // Blue is out of range again
    }

    [Fact]
    public void Squad_loses_members_and_firepower_as_its_health_drops()
    {
        var sim = NewSim();
        int squad = sim.AddUnit(Red, Vector3.Zero, maxHealth: 100, dps: 10, members: 5);
        int tank = sim.AddUnit(Blue, new Vector3(5, 0, 0), maxHealth: 1000, dps: 20);

        Assert.Equal(5, UnitById(sim, squad).Members);
        Assert.Equal(10f, UnitById(sim, squad).Dps);

        sim.Tick([new AttackCommand(Blue, tank, squad)]);
        Run(sim, T - 1); // 1 s at 20 dps: one member's 20 health

        var hit = UnitById(sim, squad);
        Assert.Equal(80f, hit.Health, 0.01f);
        Assert.Equal(4, hit.Members);
        Assert.Equal(8f, hit.Dps, 0.01f); // each lost member takes its 2 dps with it
    }

    [Fact]
    public void Destroyed_gatherer_is_removed_and_its_owner_stops_earning()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(0.5f));
        int post = sim.AddGatherer(Blue, new Vector3(5, 0, 2), maxDistance: 3);
        int raider = sim.AddUnit(Red, new Vector3(5, 0, 8), dps: 10); // 6 m away, in range

        int destroyedAt = TicksUntil(sim, new SimEvent(SimEventKind.GathererDestroyed, post), 1000,
            [new AttackCommand(Red, raider, post)]);
        Assert.InRange(destroyedAt, 599, 602); // 300 health at 10 dps
        Assert.Empty(sim.State.Gatherers);

        int earned = sim.State.Players[Blue].Gathered;
        Assert.True(earned > 0);
        Run(sim, 20 * T);
        Assert.Equal(earned, sim.State.Players[Blue].Gathered);
    }

    [Fact]
    public void Posts_pay_their_owner_and_pickups_pay_whoever_collects_them()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(0.5f));
        sim.AddGatherer(Blue, new Vector3(3, 0, 2), maxDistance: 3);
        // Red stands on both sides of a break further down and scoops the spill.
        sim.AddUnit(Red, new Vector3(10, 0, 1.9f), dps: 0);
        sim.AddUnit(Red, new Vector3(10, 0, -1.9f), dps: 0);

        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 30 * T);

        var (blue, red) = (sim.State.Players[Blue], sim.State.Players[Red]);
        Assert.True(blue.Gathered > 0);
        Assert.Equal(0, blue.Collected);
        Assert.True(red.Collected > 0);
        Assert.Equal(0, red.Gathered);
        Assert.Equal(red.Collected, red.Resources);
    }

    [Fact]
    public void Unit_types_load_from_json()
    {
        var types = GameData.ParseUnitTypes("""
            // comments and trailing commas are fine
            { "rifle_squad": { "members": 5, "speed": 4.5, "memberHealth": 20, "memberDps": 2, "range": 8 }, }
            """);

        var sim = NewSim();
        var squad = UnitById(sim, sim.AddUnit(Blue, Vector3.Zero, types["rifle_squad"]));
        Assert.Equal(5, squad.Members);
        Assert.Equal(100f, squad.MaxHealth);
        Assert.Equal(10f, squad.Dps);
        Assert.Equal(4.5f, squad.Speed);
    }
}
