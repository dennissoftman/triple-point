using System.Numerics;
using System.Runtime.InteropServices;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Trucks on supply routes: starting full, units in their way, and being shot.</summary>
public class TruckTests
{
    static BezierSegment[] Road(float length = 100) => [Straight(Vector3.Zero, new(length, 0, 0))];

    [Fact]
    public void A_route_that_starts_full_has_trucks_all_along_it_loaded_from_the_reserve()
    {
        var sim = NewSim();
        sim.AddBeltLine(Road(), Belt(5) with { Load = 6, Supply = 600, StartFull = true }); // a truck every 10 m of driving
        var line = sim.State.Belts[0];
        Assert.Equal(9, line.Packages.Count); // at 90, 80, ... 10 m
        Assert.Equal(90f, line.Packages[0].Distance, 0.01f);
        Assert.All(line.Packages, t => Assert.Equal(6, t.Cargo));
        Assert.Equal(600 - 9 * 6, line.Reserve);
        for (int i = 1; i < line.Packages.Count; i++) Assert.True(line.Packages[i - 1].Distance > line.Packages[i].Distance);

        // So a depot earns at once, not after the first truck has driven the route.
        sim.AddGatherer(Blue, new Vector3(95, 0, 2), maxDistance: 3);
        Run(sim, 5 * T);
        Assert.Equal(sim.State.Gatherers[0].Share, sim.State.Players[Blue].Gathered);
    }

    [Fact]
    public void A_unit_busy_on_the_road_holds_a_truck_up_and_an_idle_one_steps_off_it()
    {
        var sim = NewSim();
        sim.AddBeltLine(Road(), Belt(1000)); // one truck, 2 m/s
        var line = sim.State.Belts[0];
        int gunner = sim.AddUnit(Blue, new Vector3(20, 0, 0), speed: 5, dps: 10, range: 8);
        int target = sim.AddUnit(Red, new Vector3(20, 0, 7), speed: 0, maxHealth: 100000, dps: 0); // keeps it firing, standing on the road

        Run(sim, 20 * T);
        Assert.True(UnitById(sim, gunner).Firing);
        var truck = line.Packages.Single();
        Assert.True(truck.Blocked);
        Assert.InRange(truck.Distance, 14, 17); // waiting just short of it

        sim.Tick([new DestroyCommand(target)]); // nothing left to shoot: idle, it gives way
        Run(sim, 5 * T);
        var u = UnitById(sim, gunner);
        Assert.True(line.Packages.Single().Distance > 22, "the truck drove on");
        Assert.True(u.Position.Z < -1.5f, "it stepped off to the right of the way the truck drives (-Z, heading +X)");
        Run(sim, 5 * T);
        Assert.True(UnitById(sim, gunner).Position.Z < -1.5f, "and stays off the road");
    }

    [Fact]
    public void Nobody_shoots_a_truck_unordered_but_an_order_or_a_blast_can()
    {
        var sim = NewSim();
        sim.AddBeltLine(Road(), Belt(1000) with { TruckHealth = 50 });
        var line = sim.State.Belts[0];
        sim.AddUnit(Blue, new Vector3(30, 0, 5), speed: 0, dps: 50, range: 10);
        Run(sim, 30 * T); // it drives past, well within range
        Assert.Equal(50f, line.Packages.Single().Health);

        var ordered = NewSim();
        ordered.AddBeltLine(Road(), Belt(1000) with { TruckHealth = 50 });
        Run(ordered, 5 * T);
        int gun = ordered.AddUnit(Blue, new Vector3(20, 0, 5), speed: 5, dps: 50, range: 10);
        var events = new List<SimEvent>();
        ordered.Tick([new AttackCommand(Blue, gun, ordered.State.Belts[0].Packages[0].Id)]);
        for (int t = 0; t < 5 * T; t++) events.AddRange(ordered.Tick(NoCommands));
        Assert.Contains(events, e => e.Kind == SimEventKind.TruckDestroyed);
        Assert.Equal(UnitOrder.None, UnitById(ordered, gun).Current.Kind);

        // A blast aimed at the road nearby hurts it, whoever fired.
        var blast = NewSim();
        blast.AddBeltLine(Road(), Belt(1000) with { TruckHealth = 500 });
        var shell = new WeaponType(WeaponKind.Shell, Damage: 50, Reload: 5, Range: 30, ShellSpeed: 100, Hit: HitKind.Splash, SplashRadius: 4);
        int artillery = blast.AddUnit(Red, new Vector3(50, 0, 20), speed: 0, weapon: shell);
        Run(blast, 25 * T); // 50 m along: the middle of the road, where shots at it land
        blast.Tick([new AttackSegmentCommand(Red, artillery, 0, 0)]);
        Run(blast, T);
        Assert.True(blast.State.Belts[0].Packages[0].Health < 500);
    }

    [Fact]
    public void Under_fog_an_attack_on_a_truck_needs_it_in_sight()
    {
        var sim = NewSim();
        sim.EnableNavigation(-50, -50, 150, 50);
        sim.FogOfWar = true;
        sim.AddBeltLine(Road(), Belt(1000));
        Run(sim, 5 * T); // ~10 m along
        int id = sim.State.Belts[0].Packages[0].Id;
        int far = sim.AddUnit(Blue, new Vector3(10, 0, 40), speed: 5);
        CollectionsMarshal.AsSpan(sim.State.Units)[^1].Sight = 10;
        Run(sim, 1);
        sim.Tick([new AttackCommand(Blue, far, id)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, far).Current.Kind);

        int near = sim.AddUnit(Blue, sim.State.Belts[0].Packages[0].Position + new Vector3(0, 0, 6), speed: 5);
        CollectionsMarshal.AsSpan(sim.State.Units)[^1].Sight = 10;
        Run(sim, 1);
        sim.Tick([new AttackCommand(Blue, near, id)]);
        Assert.Equal(UnitOrder.Attack, UnitById(sim, near).Current.Kind);
    }
}
