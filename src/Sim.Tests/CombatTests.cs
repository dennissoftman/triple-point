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
    public void Idle_units_focus_the_weakest_enemy_in_range()
    {
        var sim = NewSim();
        sim.AddUnit(Blue, Vector3.Zero, dps: 10);
        sim.AddUnit(Blue, new Vector3(0, 0, 2), dps: 10);
        int healthy = sim.AddUnit(Red, new Vector3(3, 0, 0), maxHealth: 100, dps: 0); // nearer, full health
        int weak = sim.AddUnit(Red, new Vector3(6, 0, 0), maxHealth: 40, dps: 0);     // farther, but dies sooner

        Run(sim, T); // both Blue units put 1 s into the same target

        Assert.Equal(100f, UnitById(sim, healthy).Health);
        Assert.Equal(20f, UnitById(sim, weak).Health, 0.01f);
        Run(sim, T + 1); // the weak one dies, then they move on to the other
        Assert.DoesNotContain(sim.State.Units, u => u.Id == weak);
        Assert.True(UnitById(sim, healthy).Health < 100f);
    }

    [Fact]
    public void Tracked_vehicle_turns_nearly_on_the_spot_before_driving_off()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 3, turnRate: 60);

        // Straight behind it: it has to turn 180 degrees at 60 degrees a second.
        sim.Tick([new MoveCommand(Blue, tank, new Vector3(0, 0, -10))]);
        Run(sim, T - 1);
        var turning = UnitById(sim, tank);
        Assert.Equal(MathF.PI / 3, MathF.Abs(turning.Heading), 0.02f);
        Assert.True(turning.Position.Length() < 1f, $"moved {turning.Position.Length()} m while pivoting");

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, tank), 20 * T) > 0);
        Run(sim, T); // coast to a halt
        var parked = UnitById(sim, tank);
        Assert.True(Vector3.Distance(parked.Position, new Vector3(0, 0, -10)) < 1f);
        Assert.Equal(0f, parked.CurrentSpeed);
    }

    [Fact]
    public void Wheeled_vehicle_needs_speed_to_steer_so_it_arcs_around()
    {
        var sim = NewSim();
        int car = sim.AddUnit(Blue, Vector3.Zero, speed: 8, movement: Movement.Wheeled, acceleration: 6, turnRate: 120);

        sim.Tick([new MoveCommand(Blue, car, new Vector3(0, 0, -10))]);
        Run(sim, T - 1);
        var turning = UnitById(sim, car);
        Assert.True(turning.Position.Length() > 1.5f, $"only moved {turning.Position.Length()} m: it can't turn in place");
        Assert.True(MathF.Abs(turning.Heading) < MathF.PI * 0.9f); // still coming around

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, car), 20 * T) > 0);
    }

    [Fact]
    public void Vehicles_accelerate_instead_of_starting_at_full_speed()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 3, turnRate: 60);

        sim.Tick([new MoveCommand(Blue, tank, new Vector3(0, 0, 30))]); // straight ahead
        Run(sim, 9); // 0.5 s
        Assert.Equal(1.5f, UnitById(sim, tank).CurrentSpeed, 0.01f);
        Run(sim, 2 * T);
        Assert.Equal(4.5f, UnitById(sim, tank).CurrentSpeed, 0.01f);
    }

    [Fact]
    public void Unit_types_load_from_json()
    {
        var types = GameData.ParseUnitTypes("""
            // comments and trailing commas are fine
            {
              "rifle_squad": { "members": 5, "speed": 4.5, "memberHealth": 20, "memberDps": 2, "range": 8 },
              "tank": { "members": 1, "movement": "tracked", "speed": 4.5, "acceleration": 3, "turnRate": 60,
                        "memberHealth": 260, "memberDps": 14, "range": 9, "canCapture": false },
            }
            """);

        var sim = NewSim();
        var squad = UnitById(sim, sim.AddUnit(Blue, Vector3.Zero, types["rifle_squad"]));
        Assert.Equal(5, squad.Members);
        Assert.Equal(100f, squad.MaxHealth);
        Assert.Equal(10f, squad.Dps);
        Assert.Equal(4.5f, squad.Speed);
        Assert.Equal(Movement.Foot, squad.Movement);
        Assert.True(squad.CanCapture);

        var tank = UnitById(sim, sim.AddUnit(Blue, Vector3.Zero, types["tank"]));
        Assert.Equal(Movement.Tracked, tank.Movement);
        Assert.Equal(MathF.PI / 3, tank.TurnRate, 0.001f);
        Assert.False(tank.CanCapture);
    }
}
