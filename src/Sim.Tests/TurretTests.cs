using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>
/// Defenses and armor, with the real data: a turret can't follow what it was told to shoot, the two
/// turrets need a barracks and a factory, the heavy one is the anti-tank choice that rockets beat, and
/// the counter loop holds: rifles beat rockets, rockets beat tanks, tanks beat rifles.
/// </summary>
public class TurretTests
{
    static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "units.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("No repo root with data/units.json above the test binaries.");
    }

    static (Dictionary<string, UnitType> Units, Dictionary<string, BuildingType> Buildings) Data()
    {
        string Read(string file) => File.ReadAllText(Path.Combine(Repo(), "data", file));
        var weapons = GameData.ParseWeapons(Read("weapons.json"));
        var units = GameData.ParseUnitTypes(Read("units.json"), weapons);
        return (units, GameData.ParseBuildingTypes(Read("buildings.json"), units));
    }

    static bool Alive(Simulation sim, int id) => sim.State.Units.Exists(u => u.Id == id && u.Health > 0);

    [Fact]
    public void A_turret_drops_an_attack_order_once_its_target_is_out_of_reach()
    {
        var (units, _) = Data();
        var sim = NewSim();
        int turret = sim.AddUnit(Blue, Vector3.Zero, units["turret"]);
        int car = sim.AddUnit(Red, new Vector3(10, 0, 0), speed: 6, maxHealth: 10000, dps: 0);
        sim.Tick([new AttackCommand(Blue, turret, car)]);
        Assert.Equal(UnitOrder.Attack, UnitById(sim, turret).Current.Kind);
        sim.Tick([new MoveCommand(Red, car, new Vector3(60, 0, 0))]);
        Run(sim, 3 * T);
        Assert.Equal(UnitOrder.None, UnitById(sim, turret).Current.Kind);

        // Told to shoot something already beyond its range, it doesn't take the order up at all.
        int far = sim.AddUnit(Red, new Vector3(-30, 0, 0), speed: 0, maxHealth: 100, dps: 0);
        sim.Tick([new AttackCommand(Blue, turret, far)]);
        sim.Tick(NoCommands);
        Assert.Equal(UnitOrder.None, UnitById(sim, turret).Current.Kind);
    }

    [Fact]
    public void A_direct_hit_on_a_squad_fells_one_member_at_most()
    {
        var sim = NewSim();
        var big = new WeaponType(WeaponKind.Bullet, Damage: 50, Reload: 10, Range: 10);
        sim.AddUnit(Blue, Vector3.Zero, speed: 0, weapon: big);
        int squad = sim.AddUnit(Red, new Vector3(5, 0, 0), speed: 0, maxHealth: 100, dps: 0, members: 5);
        Run(sim, 2);
        Assert.Equal(80, UnitById(sim, squad).Health, 0.01f);
        Assert.Equal(4, UnitById(sim, squad).Members);
    }

    [Fact]
    public void The_turret_needs_a_barracks_and_the_heavy_turret_a_factory()
    {
        var (units, buildings) = Data();
        var sim = NewSim();
        sim.BuildingTypes = buildings;
        sim.State.Players[Blue].Packages = 500;
        int builder = sim.AddUnit(Blue, Vector3.Zero, units["builder"]);
        Assert.False(sim.HasRequired(Blue, buildings["turret"]));
        Assert.False(sim.HasRequired(Blue, buildings["heavy_turret"]));

        var at = new Vector3(4, 0, 0);
        Assert.Equal(Simulation.BlockedByRequirement, BlockedWhy(sim, new BuildCommand(Blue, builder, "heavy_turret", at)));

        sim.AddBuilding(Blue, new Vector3(-20, 0, 0), buildings["barracks"], built: false); // a foundation doesn't count
        Assert.False(sim.HasRequired(Blue, buildings["turret"]));
        sim.AddBuilding(Blue, new Vector3(-20, 0, 12), buildings["factory"]);
        Assert.True(sim.HasRequired(Blue, buildings["heavy_turret"]));
        Assert.False(sim.HasRequired(Red, buildings["heavy_turret"])); // someone else's doesn't
        Assert.Equal(-1, BlockedWhy(sim, new BuildCommand(Blue, builder, "heavy_turret", at)));
        Assert.Contains(sim.State.Buildings, b => b.Type.Id == "heavy_turret");
    }

    // Why the build was refused within a few seconds, or -1 if the foundation went down.
    static int BlockedWhy(Simulation sim, Command build)
    {
        var events = sim.Tick([build]);
        for (int t = 0; t < 10 * T; t++)
        {
            foreach (var e in events)
            {
                if (e.Kind == SimEventKind.BuildBlocked) return e.Index;
                if (e.Kind == SimEventKind.BuildingPlaced) return -1;
            }
            events = sim.Tick(NoCommands);
        }
        throw new Xunit.Sdk.XunitException("neither placed nor blocked");
    }

    // Attackers attack-move on a lone defense from 20 m; true if the defense is the one left standing.
    static bool DefenseHolds(string defense, string attacker, int count)
    {
        var (units, _) = Data();
        var sim = NewSim();
        int turret = sim.AddUnit(Blue, Vector3.Zero, units[defense]);
        var attackers = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int a = sim.AddUnit(Red, new Vector3(20, 0, (i - (count - 1) / 2f) * 3), units[attacker], heading: -MathF.PI / 2);
            attackers.Add(a);
            sim.Tick([new AttackMoveCommand(Red, a, Vector3.Zero)]);
        }
        for (int t = 0; t < 180 * T && Alive(sim, turret) && attackers.Exists(a => Alive(sim, a)); t++) sim.Tick(NoCommands);
        Assert.False(Alive(sim, turret) && attackers.Exists(a => Alive(sim, a)), "the fight ended");
        return Alive(sim, turret);
    }

    [Fact]
    public void The_heavy_turret_stops_a_tank_and_rifles_but_not_two_rocket_squads()
    {
        Assert.True(units_reach("heavy_turret") >= units_reach("tank"), "no tank outranges it");
        Assert.True(DefenseHolds("heavy_turret", "tank", 1));
        Assert.True(DefenseHolds("heavy_turret", "rifle_squad", 3), "rifles barely scratch armor");
        Assert.False(DefenseHolds("heavy_turret", "rocket_squad", 2));
        Assert.True(DefenseHolds("turret", "rifle_squad", 2), "infantry is what the plain turret is for");
        Assert.False(DefenseHolds("turret", "tank", 1), "and a tank is what beats it");

        static float units_reach(string id) => Data().Units[id].Gun.Range;
    }

    [Theory]
    [InlineData("rifle_squad", "rocket_squad")]
    [InlineData("rocket_squad", "tank")]
    [InlineData("tank", "rifle_squad")]
    public void The_counter_loop_holds_for_the_same_money(string winner, string loser)
    {
        // As many of each as the same packages buy (at least one), from 20 m apart, attack-moving.
        var (units, _) = Data();
        const int Budget = 40;
        Assert.Equal(winner, Fight(winner, Math.Max(1, Budget / units[winner].Cost), loser, Math.Max(1, Budget / units[loser].Cost)));
    }

    // Two groups attack-move on each other from 20 m; the type left standing.
    static string Fight(string a, int countA, string b, int countB)
    {
        var (units, _) = Data();
        var sim = NewSim();
        var (blue, red) = (new List<int>(), new List<int>());
        for (int i = 0; i < countA; i++) blue.Add(sim.AddUnit(Blue, new Vector3(-10, 0, (i - (countA - 1) / 2f) * 3), units[a], heading: MathF.PI / 2));
        for (int i = 0; i < countB; i++) red.Add(sim.AddUnit(Red, new Vector3(10, 0, (i - (countB - 1) / 2f) * 3), units[b], heading: -MathF.PI / 2));
        sim.Tick([.. blue.Select(id => (Command)new AttackMoveCommand(Blue, id, new Vector3(10, 0, 0))),
            .. red.Select(id => (Command)new AttackMoveCommand(Red, id, new Vector3(-10, 0, 0)))]);
        for (int t = 0; t < 240 * T && blue.Exists(id => Alive(sim, id)) && red.Exists(id => Alive(sim, id)); t++) sim.Tick(NoCommands);
        bool blueLeft = blue.Exists(id => Alive(sim, id)), redLeft = red.Exists(id => Alive(sim, id));
        Assert.True(blueLeft != redLeft, $"{a} x{countA} vs {b} x{countB}: the fight ended with one side left");
        return blueLeft ? a : b;
    }
}
