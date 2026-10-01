using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>
/// Garrison buildings: neutral until infantry go in, then their owner's; those inside fire out a little
/// further, can't be hit (the building takes it), come out on any order or Exit, and tumble out hurt when
/// it falls.
/// </summary>
public class GarrisonTests
{
    static readonly BuildingType House = new(Health: 900, Size: 4, Kind: BuildingKind.Garrison, Garrison: 2, Id: "house");

    static (Simulation Sim, int House) NewMap()
    {
        var sim = NewSim();
        return (sim, sim.AddBuilding(Player.None, new Vector3(10, 0, 0), House));
    }

    static Building BuildingById(Simulation sim, int id) => sim.State.Buildings.Single(b => b.Id == id);

    static int Squad(Simulation sim, int owner, Vector3 at, float dps = 10, float range = 8) =>
        sim.AddUnit(owner, at, speed: 5, maxHealth: 100, dps: dps, range: range, members: 5);

    [Fact]
    public void A_squad_goes_into_a_neutral_building_and_holds_it_hidden_until_it_comes_out()
    {
        var (sim, house) = NewMap();
        int squad = Squad(sim, Blue, Vector3.Zero);
        Assert.Equal(Player.None, BuildingById(sim, house).Owner);

        sim.Tick([new GarrisonCommand(Blue, squad, house)]);
        Run(sim, 3 * T);
        var inside = UnitById(sim, squad);
        Assert.Equal(house, inside.Inside);
        Assert.Equal(UnitOrder.None, inside.Current.Kind);
        Assert.Equal(Blue, BuildingById(sim, house).Owner);
        Assert.Equal(1, BuildingById(sim, house).Occupants);
        Assert.False(sim.SeesUnit(Red, inside));
        Assert.False(sim.TryGetTarget(squad, out _, out _)); // nothing can aim at it

        sim.Tick([new ExitCommand(Red, house)]); // only its holder empties it
        Assert.Equal(house, UnitById(sim, squad).Inside);
        int other = Squad(sim, Blue, new Vector3(6, 0, 0));
        sim.Tick([new GarrisonCommand(Blue, other, house)]);
        Run(sim, 2 * T);
        sim.Tick([new ExitCommand(Blue, house, other)]); // one of them
        Assert.Equal(-1, UnitById(sim, other).Inside);
        Assert.Equal(house, UnitById(sim, squad).Inside);
        sim.Tick([new ExitCommand(Blue, house)]);
        Assert.Equal(-1, UnitById(sim, squad).Inside);
        Assert.Equal(Player.None, BuildingById(sim, house).Owner);
        Assert.Equal(0, BuildingById(sim, house).Occupants);
    }

    [Fact]
    public void Any_order_brings_a_squad_out_and_only_infantry_of_whoever_holds_it_with_room_get_in()
    {
        var (sim, house) = NewMap();
        int a = Squad(sim, Blue, Vector3.Zero), b = Squad(sim, Blue, new Vector3(0, 0, 2)), c = Squad(sim, Blue, new Vector3(0, 0, -2));
        int red = Squad(sim, Red, new Vector3(20, 0, 0), dps: 0);
        int tank = sim.AddUnit(Blue, new Vector3(0, 0, 4), speed: 5, movement: Movement.Tracked, acceleration: 4, turnRate: 180, dps: 0);
        sim.Tick([new GarrisonCommand(Blue, a, house), new GarrisonCommand(Blue, b, house), new GarrisonCommand(Blue, tank, house)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, tank).Current.Kind); // vehicles don't go in
        Run(sim, 3 * T);
        Assert.Equal(2, BuildingById(sim, house).Occupants);

        sim.Tick([new GarrisonCommand(Blue, c, house), new GarrisonCommand(Red, red, house)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, red).Current.Kind); // it's Blue's now
        Run(sim, 3 * T);
        Assert.Equal(-1, UnitById(sim, c).Inside); // full: it stays outside

        sim.Tick([new MoveCommand(Blue, a, new Vector3(-10, 0, 0))]);
        Run(sim, 1);
        Assert.Equal(-1, UnitById(sim, a).Inside);
        Assert.Equal(1, BuildingById(sim, house).Occupants);
        Assert.Equal(Blue, BuildingById(sim, house).Owner);
        Run(sim, 3 * T);
        Assert.True(UnitById(sim, a).Position.X < 0, "out, and on its way");
    }

    [Fact]
    public void Inside_it_fires_out_further_and_the_building_takes_the_hits()
    {
        var (sim, house) = NewMap();
        int squad = Squad(sim, Blue, new Vector3(4, 0, 0), dps: 10, range: 8);
        sim.Tick([new GarrisonCommand(Blue, squad, house)]);
        Run(sim, 2 * T);
        Assert.Equal(house, UnitById(sim, squad).Inside);

        // 11.5 m from the house's middle: past the squad's 8 m, within 8 + 2 (half the house) + 2.
        int enemy = sim.AddUnit(Red, new Vector3(21.5f, 0, 0), speed: 0, maxHealth: 1000, dps: 20, range: 12);
        Run(sim, 2 * T);
        Assert.True(UnitById(sim, enemy).Health < 1000, "the squad fires out");
        Assert.Equal(100, UnitById(sim, squad).Health);
        Assert.True(BuildingById(sim, house).Health < 900, "the enemy shoots the building instead");
    }

    [Fact]
    public void When_the_building_falls_its_squads_tumble_out_at_half_health_and_nobody_shoots_an_empty_one()
    {
        var (sim, house) = NewMap();
        int squad = Squad(sim, Blue, new Vector3(4, 0, 0), dps: 0);
        int enemy = sim.AddUnit(Red, new Vector3(20, 0, 0), speed: 0, maxHealth: 1000, dps: 50, range: 12);
        Run(sim, 2 * T);
        Assert.Equal(900, BuildingById(sim, house).Health); // empty: nobody's, so nobody's target

        sim.Tick([new GarrisonCommand(Blue, squad, house)]);
        for (int t = 0; t < 40 * T && sim.State.Buildings.Exists(b => b.Id == house); t++) sim.Tick(NoCommands);
        Assert.DoesNotContain(sim.State.Buildings, b => b.Id == house);
        var outside = UnitById(sim, squad);
        Assert.Equal(-1, outside.Inside);
        Assert.Equal(50, outside.Health, 0.01f);
    }
}
