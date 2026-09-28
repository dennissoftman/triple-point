using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Units answering fire from enemies they can't reach from where they stand.</summary>
public class ReturnFireTests
{
    [Fact]
    public void Idle_unit_hit_from_out_of_range_chases_the_attacker_kills_it_and_walks_back()
    {
        var sim = NewSim();
        var cannon = new WeaponType(WeaponKind.Shell, Damage: 10, Reload: 1, Range: 14, ShellSpeed: 40);
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, maxHealth: 300, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(13, 0, 0), speed: 0, weapon: cannon); // outranges Blue

        int diedAt = TicksUntil(sim, new SimEvent(SimEventKind.UnitDied, red), 20 * T);

        Assert.True(diedAt > 0);
        Assert.InRange(UnitById(sim, blue).Position.X, 4.9f, 5.4f); // closed to its 8 m, no further
        Run(sim, 3 * T);
        var back = UnitById(sim, blue);
        Assert.Equal(Vector3.Zero, back.Position);
        Assert.False(back.Returning);
        Assert.Equal(UnitOrder.None, back.Current.Kind);
    }

    [Fact]
    public void Chase_gives_up_at_the_leash_and_the_unit_walks_back()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, maxHealth: 300, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(13, 0, 0), speed: 4, dps: 5, range: 14);

        // Red shoots while backing off, a little slower than Blue: Blue can't close to 8 m within 15 m of its spot.
        sim.Tick([new MoveCommand(Red, red, new Vector3(100, 0, 0))]);
        float furthest = 0;
        for (int i = 0; i < 12 * T; i++)
        {
            sim.Tick(NoCommands);
            furthest = MathF.Max(furthest, UnitById(sim, blue).Position.X);
        }

        Assert.InRange(furthest, 15f, 15.5f);
        Assert.Equal(Vector3.Zero, UnitById(sim, blue).Position);
        Assert.Equal(100f, UnitById(sim, red).Health); // never got in range
    }

    [Fact]
    public void A_move_order_is_never_diverted_by_fire()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 1, maxHealth: 500, dps: 10, range: 8);
        sim.AddUnit(Red, new Vector3(10, 0, 0), speed: 0, dps: 10, range: 14);

        // Hit for the first ~10 s of a 20 s walk straight away from the side Red is on.
        sim.Tick([new MoveCommand(Blue, blue, new Vector3(0, 0, -20))]);
        for (int i = 0; i < 25 * T; i++)
        {
            sim.Tick(NoCommands);
            Assert.Equal(0f, UnitById(sim, blue).Position.X);
        }
        Assert.Equal(new Vector3(0, 0, -20), UnitById(sim, blue).Position);
        Assert.True(UnitById(sim, blue).Health < 500);
    }

    [Fact]
    public void Units_holding_a_switch_stay_and_only_turn_toward_the_attacker()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, -5))], Belt(1));
        sim.AddBeltLine([Straight(new(10, 0, 0), new(20, 0, 5))], Belt(1));
        sim.AddJunction(new Vector3(10, 0, 0), attachRadius: 0.5f);
        int blue = sim.AddUnit(Blue, new Vector3(10, 0, 2), maxHealth: 300, dps: 10, range: 8);
        sim.AddUnit(Red, new Vector3(23, 0, 2), speed: 0, dps: 5, range: 14);

        Run(sim, 3 * T);

        var held = UnitById(sim, blue);
        Assert.Equal(new Vector3(10, 0, 2), held.Position);
        Assert.Equal(MathF.PI / 2, held.Turret, 0.01f); // facing +x, where the fire comes from
        Assert.True(held.Health < 300);
    }

    [Fact]
    public void Idle_allies_close_by_answer_with_the_unit_under_fire_and_far_ones_dont()
    {
        var sim = NewSim();
        int hit = sim.AddUnit(Blue, Vector3.Zero, speed: 5, maxHealth: 300, dps: 10, range: 8);
        int near = sim.AddUnit(Blue, new Vector3(-3, 0, 0), speed: 5, maxHealth: 300, dps: 10, range: 8); // out of Red's range
        int far = sim.AddUnit(Blue, new Vector3(-20, 0, 0), speed: 5, maxHealth: 300, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(13, 0, 0), speed: 0, dps: 5, range: 14);

        Run(sim, T);

        Assert.True(UnitById(sim, hit).Position.X > 2);
        Assert.True(UnitById(sim, near).Position.X > -1);
        Assert.Equal(red, UnitById(sim, near).RespondTo);
        Assert.Equal(new Vector3(-20, 0, 0), UnitById(sim, far).Position);
    }

    [Fact]
    public void Attack_move_turns_on_an_attacker_out_of_range_then_carries_on()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, maxHealth: 300, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(12, 0, 0), speed: 0, maxHealth: 50, dps: 10, range: 14);

        sim.Tick([new AttackMoveCommand(Blue, blue, new Vector3(0, 0, -30))]);
        float furthest = 0;
        int arrivedAt = -1;
        for (int i = 1; i <= 30 * T && arrivedAt < 0; i++)
        {
            if (sim.Tick(NoCommands).Contains(new SimEvent(SimEventKind.UnitArrived, blue))) arrivedAt = i;
            furthest = MathF.Max(furthest, UnitById(sim, blue).Position.X);
        }

        Assert.True(furthest > 3);                                  // went for Red...
        Assert.DoesNotContain(sim.State.Units, u => u.Id == red);  // ...killed it...
        Assert.True(arrivedAt > 0);                                 // ...and still got where it was going
        Assert.Equal(new Vector3(0, 0, -30), UnitById(sim, blue).Position);
    }
}
