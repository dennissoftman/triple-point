using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>
/// Nothing heals on its own: units mend at home (infantry at a barracks, vehicles at a factory), paying as
/// they heal; builders and engineers repair buildings, posts and defenses; auto-retreat sends a badly hurt
/// unit to mend.
/// </summary>
public class MendTests
{
    static readonly BuildingType Barracks = new(Health: 600, Size: 4, Mends: TargetClass.Infantry, MendSeconds: 10, MendShare: 0.5f, Id: "barracks");
    static readonly BuildingType Factory = new(Health: 800, Size: 6, Cost: 20, BuildTime: 20, Mends: TargetClass.Vehicle, MendSeconds: 10, MendShare: 0.5f, Id: "factory");
    static readonly UnitType Squad = new(Members: 5, Speed: 5, MemberHealth: 20, Cost: 20, Id: "squad") { Gun = WeaponType.Unarmed };
    static readonly UnitType Tank = new(Members: 1, Speed: 5, MemberHealth: 200, Movement: Movement.Tracked, Acceleration: 4, TurnRate: 180, Cost: 20, Id: "tank") { Gun = WeaponType.Unarmed };
    static readonly UnitType Builder = new(Members: 1, Speed: 5, MemberHealth: 100, Builds: [], RepairSeconds: 5, RepairCost: 2, Id: "builder") { Gun = WeaponType.Unarmed };

    static Simulation NewGame(int packages = 100)
    {
        var sim = NewSim();
        sim.BuildingTypes = new Dictionary<string, BuildingType> { ["barracks"] = Barracks, ["factory"] = Factory };
        sim.State.Players[Blue].Packages = packages;
        return sim;
    }

    static void Hurt(Simulation sim, int id, float health)
    {
        int i = sim.State.Units.FindIndex(u => u.Id == id);
        var u = sim.State.Units[i];
        u.Health = health;
        sim.State.Units[i] = u;
    }

    [Fact]
    public void A_hurt_squad_sent_to_its_barracks_heals_and_gets_its_members_back_paid_as_it_goes()
    {
        var sim = NewGame();
        int barracks = sim.AddBuilding(Blue, new Vector3(10, 0, 0), Barracks);
        int squad = sim.AddUnit(Blue, Vector3.Zero, Squad);
        Hurt(sim, squad, 20); // one member of five left
        Assert.Equal(1, UnitById(sim, squad).Members);

        sim.Tick([new MendCommand(Blue, squad, barracks)]);
        Assert.Equal(UnitOrder.Mend, UnitById(sim, squad).Current.Kind);
        Run(sim, 15 * T);
        var mended = UnitById(sim, squad);
        Assert.Equal(mended.MaxHealth, mended.Health);
        Assert.Equal(5, mended.Members);
        Assert.Equal(UnitOrder.None, mended.Current.Kind);
        // 80 of 100 health back at 20 × 0.5 = 10 packages for a full mend: 8, give or take a package.
        Assert.InRange(100 - sim.State.Players[Blue].Packages, 7, 9);
    }

    [Fact]
    public void Infantry_mends_only_at_a_barracks_and_vehicles_only_at_a_factory()
    {
        var sim = NewGame();
        int barracks = sim.AddBuilding(Blue, new Vector3(10, 0, 0), Barracks);
        int factory = sim.AddBuilding(Blue, new Vector3(-12, 0, 0), Factory);
        int squad = sim.AddUnit(Blue, Vector3.Zero, Squad);
        int tank = sim.AddUnit(Blue, new Vector3(0, 0, 5), Tank);
        Hurt(sim, squad, 50);
        Hurt(sim, tank, 50);
        sim.Tick([new MendCommand(Blue, squad, factory), new MendCommand(Blue, tank, barracks)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, squad).Current.Kind);
        Assert.Equal(UnitOrder.None, UnitById(sim, tank).Current.Kind);

        sim.Tick([new MendCommand(Blue, tank, factory), new MendCommand(Red, squad, barracks)]); // only its owner sends it
        Assert.Equal(UnitOrder.Mend, UnitById(sim, tank).Current.Kind);
        Assert.Equal(UnitOrder.None, UnitById(sim, squad).Current.Kind);
        Run(sim, 20 * T);
        Assert.Equal(200, UnitById(sim, tank).Health);
    }

    [Fact]
    public void Nothing_heals_on_its_own_and_mending_stalls_while_broke()
    {
        var sim = NewGame(packages: 0);
        int barracks = sim.AddBuilding(Blue, new Vector3(6, 0, 0), Barracks);
        int squad = sim.AddUnit(Blue, Vector3.Zero, Squad);
        Hurt(sim, squad, 40);
        Run(sim, 10 * T);
        Assert.Equal(40, UnitById(sim, squad).Health); // standing by its barracks does nothing

        sim.Tick([new MendCommand(Blue, squad, barracks)]);
        Run(sim, 10 * T);
        Assert.Equal(40, UnitById(sim, squad).Health, 0.5f);
        Assert.Equal(UnitOrder.Mend, UnitById(sim, squad).Current.Kind); // it waits there for the money

        sim.State.Players[Blue].Packages = 50;
        Run(sim, 15 * T);
        Assert.Equal(100, UnitById(sim, squad).Health);
    }

    [Fact]
    public void A_builder_repairs_its_owners_building_and_defense_paid_as_it_goes()
    {
        var sim = NewGame();
        int factory = sim.AddBuilding(Blue, new Vector3(12, 0, 0), Factory);
        var building = sim.State.Buildings.Single(b => b.Id == factory);
        building.Health = 400;
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);

        sim.Tick([new RepairCommand(Blue, builder, factory)]);
        Assert.Equal(UnitOrder.Repair, UnitById(sim, builder).Current.Kind);
        Run(sim, 20 * T); // a full repair takes half its 20 s build time
        Assert.Equal(800, building.Health);
        // Half its health back, at half its cost of 20 for a full repair: 5 packages, give or take one.
        Assert.InRange(100 - sim.State.Players[Blue].Packages, 4, 6);

        int gun = sim.AddUnit(Blue, new Vector3(-6, 0, 0), speed: 0, maxHealth: 300, dps: 0, movement: Movement.Static);
        Hurt(sim, gun, 100);
        sim.Tick([new RepairCommand(Blue, builder, gun)]);
        Run(sim, 15 * T);
        Assert.Equal(300, UnitById(sim, gun).Health);

        // Only a repairer takes the order, and only for its owner's things.
        int squad = sim.AddUnit(Blue, Vector3.Zero, Squad);
        building.Health = 700;
        sim.Tick([new RepairCommand(Blue, squad, factory), new RepairCommand(Red, builder, factory)]);
        Assert.Equal(UnitOrder.None, UnitById(sim, squad).Current.Kind);
        Assert.Equal(UnitOrder.None, UnitById(sim, builder).Current.Kind);
    }

    [Fact]
    public void Auto_retreat_sends_a_badly_hurt_unit_to_mend_and_is_off_until_asked()
    {
        var sim = NewGame();
        int factory = sim.AddBuilding(Blue, new Vector3(-20, 0, 0), Factory);
        int tank = sim.AddUnit(Blue, Vector3.Zero, Tank);
        int other = sim.AddUnit(Blue, new Vector3(0, 0, 6), Tank);
        sim.Tick([new MoveCommand(Blue, tank, new Vector3(30, 0, 0)), new MoveCommand(Blue, other, new Vector3(30, 0, 6)),
            new SetRetreatCommand(Blue, tank, true)]);
        Hurt(sim, tank, 50);  // 25%
        Hurt(sim, other, 50);
        Run(sim, T);
        Assert.Equal(UnitOrder.Mend, UnitById(sim, tank).Current.Kind);
        Assert.Equal(factory, UnitById(sim, tank).Current.TargetId);
        Assert.Equal(UnitOrder.Move, UnitById(sim, other).Current.Kind); // off by default: it carries on
        Run(sim, 30 * T);
        Assert.Equal(200, UnitById(sim, tank).Health);
        Assert.Equal(UnitOrder.None, UnitById(sim, tank).Current.Kind); // mended, it waits there
    }
}
