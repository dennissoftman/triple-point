using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Buildings training units: paying as they build, the queue, repeat, rally points.</summary>
public class ProductionTests
{
    static readonly WeaponType Gun = new(WeaponKind.Bullet, Damage: 1, Reload: 0.5f, Range: 8);
    static readonly UnitType Squad = new(Members: 5, Speed: 5, MemberHealth: 20, Weapon: "gun", Cost: 4, BuildTime: 1, Id: "squad") { Gun = Gun };
    static readonly UnitType Tank = new(Members: 1, Speed: 4, MemberHealth: 200, Weapon: "gun", Cost: 10, BuildTime: 2, Id: "tank") { Gun = Gun };
    static readonly BuildingType Hq = new(Health: 500, Size: 4, Produces: ["squad", "tank"], QueueLimit: 3, Id: "hq") { Units = [Squad, Tank] };

    // Blue's HQ at the origin, its exit toward +z (at 3.5 m), with `resources` to spend.
    static (Simulation sim, int hq) NewHq(int resources)
    {
        var sim = NewSim();
        sim.State.Players[Blue].Resources = resources;
        return (sim, sim.AddBuilding(Blue, Vector3.Zero, Hq));
    }

    static Building BuildingById(Simulation sim, int id) => sim.State.Buildings.Single(b => b.Id == id);

    [Fact]
    public void Cost_is_paid_as_the_unit_builds_and_it_leaves_by_the_exit_when_done()
    {
        var (sim, hq) = NewHq(10);

        sim.Tick([new ProduceCommand(Blue, hq, "squad")]);
        Assert.Equal(9, sim.State.Players[Blue].Resources); // the first share is due at once
        Run(sim, 9);
        Assert.Equal(8, sim.State.Players[Blue].Resources); // half built, half paid
        Assert.Empty(sim.State.Units);

        var events = sim.Tick(NoCommands);
        for (int i = 11; i < 20; i++) events = sim.Tick(NoCommands);
        var squad = Assert.Single(sim.State.Units);
        Assert.Contains(new SimEvent(SimEventKind.UnitProduced, squad.Id, hq), events);
        Assert.Equal(6, sim.State.Players[Blue].Resources); // 4 in all
        Assert.Equal(new Vector3(0, 0, 3.5f), squad.Position);
        Assert.Equal(Blue, squad.Owner);
        Assert.Empty(BuildingById(sim, hq).Queue);
    }

    [Fact]
    public void Production_stalls_while_the_owner_is_broke_and_resumes_when_paid()
    {
        var (sim, hq) = NewHq(1);

        sim.Tick([new ProduceCommand(Blue, hq, "squad")]);
        Run(sim, 40);
        var building = BuildingById(sim, hq);
        Assert.True(building.Stalled);
        Assert.Equal(5, building.Progress);  // the 1 paid covers the first 5 of 20 ticks
        Assert.Empty(sim.State.Units);

        sim.State.Players[Blue].Resources = 3;
        Run(sim, 15);
        Assert.Single(sim.State.Units);
        Assert.Equal(0, sim.State.Players[Blue].Resources);
    }

    [Fact]
    public void Repeat_loops_the_whole_queue_so_a_mix_keeps_its_ratio()
    {
        var (sim, hq) = NewHq(1000);

        sim.Tick([new ProduceCommand(Blue, hq, "squad"), new ProduceCommand(Blue, hq, "tank"), new SetRepeatCommand(Blue, hq, true)]);
        var produced = new List<string>();
        for (int i = 0; i < 9 * T; i++)
            foreach (var e in sim.Tick(NoCommands))
                if (e.Kind == SimEventKind.UnitProduced) produced.Add(UnitById(sim, e.Id).Type);

        Assert.Equal(["squad", "tank", "squad", "tank", "squad", "tank"], produced); // 1 s + 2 s, three times
        Assert.Equal(["squad", "tank"], BuildingById(sim, hq).Queue.Select(t => t.Id));
    }

    [Fact]
    public void Cancelling_the_unit_in_production_refunds_what_was_paid()
    {
        var (sim, hq) = NewHq(20);

        sim.Tick([new ProduceCommand(Blue, hq, "tank"), new ProduceCommand(Blue, hq, "squad")]);
        Run(sim, 19); // half the tank: 5 paid
        Assert.Equal(15, sim.State.Players[Blue].Resources);

        sim.Tick([new CancelProductionCommand(Blue, hq, 0)]);
        var building = BuildingById(sim, hq);
        Assert.Equal(["squad"], building.Queue.Select(t => t.Id)); // the squad moved up and started
        Assert.Equal(1, building.Progress);
        Assert.Equal(19, sim.State.Players[Blue].Resources);       // 20, minus the squad's first share
    }

    [Fact]
    public void Queue_takes_only_what_the_building_produces_up_to_its_limit_and_only_from_its_owner()
    {
        var (sim, hq) = NewHq(0);

        sim.Tick([
            new ProduceCommand(Red, hq, "squad"),     // not Red's building
            new ProduceCommand(Blue, hq, "artillery"), // not something it produces
            new ProduceCommand(Blue, hq, "squad"),
            new ProduceCommand(Blue, hq, "squad"),
            new ProduceCommand(Blue, hq, "tank"),
            new ProduceCommand(Blue, hq, "tank"),      // over the limit of 3
        ]);

        Assert.Equal(["squad", "squad", "tank"], BuildingById(sim, hq).Queue.Select(t => t.Id));
    }

    [Fact]
    public void Finished_units_head_for_the_rally_point_and_spread_out_around_it()
    {
        var (sim, hq) = NewHq(100);
        var rally = new Vector3(10, 0, 10);

        sim.Tick([new SetRallyCommand(Blue, hq, rally), new ProduceCommand(Blue, hq, "squad"), new ProduceCommand(Blue, hq, "squad")]);
        Run(sim, 2 * T + 5 * T);

        var squads = sim.State.Units;
        Assert.Equal(2, squads.Count);
        Assert.Equal(rally, squads[0].Position);                                  // the first on the point
        Assert.Equal(2.5f, Vector3.Distance(squads[1].Position, rally), 0.01f);  // the next beside it
    }

    [Fact]
    public void Buildings_are_targets_and_a_destroyed_one_stops_producing()
    {
        var (sim, hq) = NewHq(100);
        int red = sim.AddUnit(Red, new Vector3(0, 0, -10), dps: 1000, range: 8); // not in the exit's path

        sim.Tick([new ProduceCommand(Blue, hq, "tank"), new AttackCommand(Red, red, hq)]);
        int destroyedAt = TicksUntil(sim, new SimEvent(SimEventKind.BuildingDestroyed, hq), 2 * T);

        Assert.True(destroyedAt > 0);
        Assert.Empty(sim.State.Buildings);
        Run(sim, 3 * T);
        Assert.DoesNotContain(sim.State.Units, u => u.Owner == Blue);
    }

    [Fact]
    public void Building_types_load_from_json_with_the_units_they_produce()
    {
        var units = new Dictionary<string, UnitType> { ["squad"] = Squad };
        var types = GameData.ParseBuildingTypes("""{ "hq": { "health": 100, "size": 4, "produces": ["squad"] } }""", units);

        Assert.Equal("hq", types["hq"].Id);
        Assert.Equal(5, types["hq"].QueueLimit); // the default
        Assert.Same(Squad, Assert.Single(types["hq"].Units));
        Assert.Throws<InvalidDataException>(() => GameData.ParseBuildingTypes("""{ "hq": { "health": 100, "size": 4, "produces": ["mech"] } }""", units));
        Assert.Throws<InvalidDataException>(() => GameData.ParseBuildingTypes("""{ "hq": { "health": 0, "size": 4, "produces": [] } }""", units));
    }
}
