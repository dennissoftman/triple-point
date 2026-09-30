using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Stop drops every order; holding position also keeps the unit on its spot, whatever happens.</summary>
public class StopAndHoldTests
{
    [Fact]
    public void Stop_drops_the_current_and_queued_orders_and_the_unit_stays_there()
    {
        var sim = NewSim();
        int unit = sim.AddUnit(Blue, Vector3.Zero, speed: 5);
        sim.Tick([new MoveCommand(Blue, unit, new(20, 0, 0)), new MoveCommand(Blue, unit, new(20, 0, 20), Queued: true)]);
        Run(sim, T);
        sim.Tick([new StopCommand(Blue, unit)]);
        var stopped = UnitById(sim, unit);
        Assert.Equal(UnitOrder.None, stopped.Current.Kind);
        Assert.Empty(stopped.Pending);
        Assert.False(stopped.Holding);
        Run(sim, 3 * T);
        Assert.Equal(stopped.Position.X, UnitById(sim, unit).Position.X, 0.3f);

        sim.Tick([new StopCommand(Red, unit)]); // only its owner stops it
        sim.Tick([new MoveCommand(Blue, unit, new(20, 0, 0)), new StopCommand(Red, unit)]);
        Assert.Equal(UnitOrder.Move, UnitById(sim, unit).Current.Kind);
    }

    [Fact]
    public void A_unit_holding_position_fires_at_what_comes_in_range_but_never_chases_an_attacker_it_cannot_reach()
    {
        var sim = NewSim();
        var cannon = new WeaponType(WeaponKind.Shell, Damage: 10, Reload: 1, Range: 14, ShellSpeed: 40);
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, maxHealth: 300, dps: 10, range: 8);
        sim.Tick([new HoldCommand(Blue, blue)]);
        Assert.True(UnitById(sim, blue).Holding);
        sim.AddUnit(Red, new Vector3(13, 0, 0), speed: 0, weapon: cannon); // outranges it: an idle unit would chase (ReturnFireTests)
        Run(sim, 5 * T);
        var held = UnitById(sim, blue);
        Assert.True(held.Health < 300, "it was shot at");
        Assert.Equal(0f, held.Position.X, 0.05f); // and stayed put

        int near = sim.AddUnit(Red, new Vector3(0, 0, 6), speed: 0, maxHealth: 50, dps: 0);
        int diedAt = TicksUntil(sim, new SimEvent(SimEventKind.UnitDied, near), 10 * T);
        Assert.True(diedAt > 0, "what comes in range, it shoots");

        sim.Tick([new MoveCommand(Blue, blue, new(-10, 0, 0))]); // any order ends it
        Assert.False(UnitById(sim, blue).Holding);
    }

    [Fact]
    public void A_unit_holding_position_on_a_road_holds_trucks_up_and_is_not_shoved_aside()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(100, 0, 0))], Belt(1000)); // one truck, 2 m/s
        int blocker = sim.AddUnit(Blue, new Vector3(20, 0, 0), speed: 5, dps: 0);
        sim.Tick([new HoldCommand(Blue, blocker)]);
        Run(sim, 20 * T);
        var truck = sim.State.Belts[0].Packages.Single();
        Assert.True(truck.Blocked);
        Assert.InRange(truck.Distance, 14, 17);
        Assert.Equal(Vector3.Zero with { X = 20 }, UnitById(sim, blocker).Position); // an idle one would have stepped off

        sim.Tick([new StopCommand(Blue, blocker)]); // now idle: it gives way
        Run(sim, 5 * T);
        Assert.True(sim.State.Belts[0].Packages.Single().Distance > 20);
    }
}
