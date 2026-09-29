using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Builders putting up buildings: foundations, paying as they grow, placement, posts and defenses.</summary>
public class ConstructionTests
{
    static readonly WeaponType Gun = new(WeaponKind.Bullet, Damage: 1, Reload: 0.1f, Range: 10);
    static readonly UnitType Squad = new(Members: 1, Speed: 5, MemberHealth: 100, Weapon: "gun", Id: "squad") { Gun = Gun };
    static readonly UnitType Gunner = new(Members: 1, Speed: 0, MemberHealth: 200, Weapon: "gun", Movement: Movement.Static, Id: "gunner") { Gun = Gun };
    static readonly UnitType Builder = new(Members: 1, Speed: 5, MemberHealth: 100, Builds: ["barracks", "post", "gunner"], Id: "builder") { Gun = WeaponType.Unarmed };
    static readonly BuildingType Barracks = new(Health: 500, Size: 4, Produces: ["squad"], Cost: 10, BuildTime: 1, Id: "barracks") { Units = [Squad] };
    static readonly BuildingType Post = new(Health: 300, Size: 2, Cost: 4, BuildTime: 1, Kind: BuildingKind.Post, Id: "post");
    static readonly BuildingType GunnerNest = new(Health: 400, Size: 2, Cost: 4, BuildTime: 1, Kind: BuildingKind.Defense, Unit: "gunner", Id: "gunner") { Defense = Gunner };
    static readonly BuildingType Hq = new(Health: 1000, Size: 4, Id: "hq"); // no builder builds it

    static Simulation NewConstructionSim(int packages = 100)
    {
        var sim = NewSim();
        sim.State.Players[Blue].Packages = packages;
        sim.BuildingTypes = new Dictionary<string, BuildingType> { ["barracks"] = Barracks, ["post"] = Post, ["gunner"] = GunnerNest, ["hq"] = Hq };
        return sim;
    }

    static Building BuildingAt(Simulation sim, Vector3 at) => sim.State.Buildings.Single(b => b.Position == at);

    [Fact]
    public void Builder_walks_to_the_site_lays_the_foundation_and_builds_it_paying_as_it_grows()
    {
        var sim = NewConstructionSim(packages: 20);
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);
        var site = new Vector3(10, 0, 0);

        // Works from within 2 m of the 4 m footprint's edge: 6 m of walking at 5 m/s, 24 ticks.
        sim.Tick([new BuildCommand(Blue, builder, "barracks", site)]);
        int placedAt = TicksUntil(sim, new SimEvent(SimEventKind.BuildingPlaced, sim.State.Units[0].Id + 1, builder), 2 * T) + 1;
        Assert.InRange(placedAt, 24, 26);
        var foundation = BuildingAt(sim, site);
        Assert.False(foundation.Built);
        Assert.Equal(1, foundation.BuildProgress); // laid and worked the same tick
        Assert.Equal(50f + 450f / 20, foundation.Health, 0.1f); // a tenth of its health to start with, plus a tick's growth

        sim.Tick([new ProduceCommand(Blue, foundation.Id, "squad")]); // foundations don't take orders to produce
        Assert.Empty(foundation.Queue);
        int doneAt = TicksUntil(sim, new SimEvent(SimEventKind.BuildingCompleted, foundation.Id, foundation.Id), 2 * T) + 1;
        Assert.InRange(doneAt, 18, 20); // 20 ticks of work in all (1 s)
        Assert.True(foundation.Built);
        Assert.Equal(500f, foundation.Health, 1f);
        Assert.Equal(10, sim.State.Players[Blue].Packages);

        sim.Tick(NoCommands);
        Assert.Equal(UnitOrder.None, UnitById(sim, builder).Current.Kind); // done
    }

    [Fact]
    public void A_foundation_grows_only_while_a_builder_works_on_it_and_a_second_one_does_not_speed_it_up()
    {
        var sim = NewConstructionSim();
        var site = new Vector3(4, 0, 0);
        int a = sim.AddUnit(Blue, Vector3.Zero, Builder); // already within reach
        int b = sim.AddUnit(Blue, new Vector3(0, 0, 1), Builder);

        sim.Tick([new BuildCommand(Blue, a, "barracks", site)]);
        var foundation = BuildingAt(sim, site);
        sim.Tick([new ResumeBuildCommand(Blue, b, foundation.Id)]);
        Run(sim, 8);
        Assert.Equal(10, foundation.BuildProgress); // one tick of work per tick, whoever's working

        sim.Tick([new MoveCommand(Blue, a, new Vector3(-40, 0, 0)), new MoveCommand(Blue, b, new Vector3(-40, 0, 1))]);
        Run(sim, 20);
        Assert.Equal(10, foundation.BuildProgress); // abandoned: nothing happens

        sim.Tick([new ResumeBuildCommand(Blue, b, foundation.Id)]);
        int doneAt = TicksUntil(sim, new SimEvent(SimEventKind.BuildingCompleted, foundation.Id, foundation.Id), 20 * T);
        Assert.True(doneAt > 0);
        Assert.True(foundation.Built);
    }

    [Fact]
    public void Construction_stalls_when_the_money_runs_out_after_it_started()
    {
        var sim = NewConstructionSim(packages: 10); // enough to start the 10-package barracks
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);

        sim.Tick([new BuildCommand(Blue, builder, "barracks", new Vector3(4, 0, 0))]); // laid, and 1 paid
        sim.State.Players[Blue].Packages = 1;                                          // spent elsewhere
        Run(sim, 20);
        var foundation = sim.State.Buildings.Single();
        Assert.Equal(4, foundation.BuildProgress); // 2 paid in all covers 4 of its 20 ticks
        Assert.True(foundation.BuildStalled);
        Assert.Equal(0, sim.State.Players[Blue].Packages);
    }

    [Fact]
    public void A_building_its_owner_cannot_afford_in_full_is_never_started()
    {
        var sim = NewConstructionSim(packages: 9);
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);

        int blockedAt = TicksUntil(sim, new SimEvent(SimEventKind.BuildBlocked, builder, Simulation.BlockedByMoney), T,
            [new BuildCommand(Blue, builder, "barracks", new Vector3(4, 0, 0))]);

        Assert.True(blockedAt > 0);
        Assert.Empty(sim.State.Buildings);
        Assert.Equal(9, sim.State.Players[Blue].Packages);
    }

    [Fact]
    public void Posts_snap_beside_the_nearest_belt_on_the_cursor_side_facing_it()
    {
        var sim = NewConstructionSim();
        sim.AddBeltLine(TwoSegments(), Belt(1)); // along x, from 0 to 20

        Assert.True(sim.SnapToBelt(new Vector3(10.3f, 0, 3), 6, out var at, out float heading));
        Assert.Equal(new Vector3(10, 0, 2.5f), at);   // a whole meter along, 2.5 m off the middle
        Assert.Equal(MathF.PI, MathF.Abs(heading), 0.001f); // facing -z, toward the belt
        Assert.True(sim.CanPlace(Post, at));

        Assert.True(sim.SnapToBelt(new Vector3(4.6f, 0, -1), 6, out at, out heading));
        Assert.Equal(new Vector3(5, 0, -2.5f), at);   // the other side
        Assert.Equal(0f, heading, 0.001f);

        Assert.False(sim.SnapToBelt(new Vector3(10, 0, 9), 6, out _, out _)); // no belt that close
    }

    [Fact]
    public void A_site_taken_by_the_time_the_builder_gets_there_blocks_the_build()
    {
        var sim = NewConstructionSim();
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);
        sim.Tick([new BuildCommand(Blue, builder, "barracks", new Vector3(20, 0, 0))]);
        sim.AddBuilding(Red, new Vector3(22, 0, 0), Hq); // overlaps the site

        int blockedAt = TicksUntil(sim, new SimEvent(SimEventKind.BuildBlocked, builder, Simulation.BlockedByTheSite), 5 * T);

        Assert.True(blockedAt > 0);
        Assert.Single(sim.State.Buildings);
        Assert.Equal(UnitOrder.None, UnitById(sim, builder).Current.Kind);
    }

    [Fact]
    public void Buildings_keep_clear_of_belts_and_posts_stand_beside_one()
    {
        var sim = NewConstructionSim();
        sim.AddBeltLine(TwoSegments(), Belt(1)); // along x, from 0 to 20

        Assert.False(sim.CanPlace(Barracks, new Vector3(10, 0, 2)));  // its footprint would cover the belt
        Assert.True(sim.CanPlace(Barracks, new Vector3(10, 0, 4)));
        Assert.False(sim.CanPlace(Post, new Vector3(10, 0, 1)));      // on the belt
        Assert.True(sim.CanPlace(Post, new Vector3(10, 0, 2)));
        Assert.False(sim.CanPlace(Post, new Vector3(10, 0, 5)));      // out of reach of it
    }

    [Fact]
    public void Posts_keep_the_post_spacing_along_the_line_from_any_post_or_post_foundation()
    {
        var sim = NewConstructionSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(100, 0, 0))], Belt(1));
        sim.AddBeltLine([Straight(new(0, 0, 10), new(100, 0, 10))], Belt(1)); // another line, 10 m over
        sim.AddGatherer(Red, new Vector3(20, 0, 2), maxDistance: 3);           // anyone's post counts

        float s = Simulation.PostSpacing;
        Assert.False(sim.CanPlace(Post, new Vector3(20 + s - 1, 0, 2)));
        Assert.False(sim.CanPlace(Post, new Vector3(20, 0, -2)));      // the other side of the belt is no escape
        Assert.True(sim.CanPlace(Post, new Vector3(20 + s + 1, 0, 2)));
        Assert.True(sim.CanPlace(Post, new Vector3(20, 0, 8)));        // beside the other line: its own spacing

        // A foundation holds its spot too, before it's a post.
        sim.AddBuilding(Blue, new Vector3(90, 0, 2), Post, built: false);
        Assert.False(sim.CanPlace(Post, new Vector3(90 - s + 1, 0, 2)));
        Assert.True(sim.CanPlace(Post, new Vector3(90 - s - 1, 0, 2))); // and clear of Red's at 20
    }

    [Fact]
    public void Post_spots_are_the_open_belt_clear_of_the_spacing_and_a_post_snaps_to_the_nearest()
    {
        var sim = NewConstructionSim();
        // 0-100 m, the first and last 10 m covered; a post at 40.
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0)), Straight(new(10, 0, 0), new(90, 0, 0)), Straight(new(90, 0, 0), new(100, 0, 0))],
            Belt(1), coveredStart: 10, coveredEnd: 10);
        sim.AddGatherer(Red, new Vector3(40, 0, 2), maxDistance: 3);

        var spots = new List<(int Line, float From, float To)>();
        sim.PostSpots(spots);
        var free = Assert.Single(spots); // 10-70 is within the spacing of the post at 40
        Assert.Equal((0, 70f, 90f), (free.Line, free.From, free.To), new SpotComparer());

        // Aiming at 55 (too close to the post at 40) lands at the nearest free spot along the belt, 70.
        Assert.True(sim.SnapPost(new Vector3(55, 0, 2), 6, out var at, out _));
        Assert.Equal(70f, at.X, 0.1f);
        Assert.True(sim.CanPlace(Post, at));
        // Where it's free already, it stays under the cursor.
        Assert.True(sim.SnapPost(new Vector3(80, 0, -2), 6, out at, out _));
        Assert.Equal((80f, -Simulation.PostOffset), (at.X, at.Z));
    }

    sealed class SpotComparer : IEqualityComparer<(int, float, float)>
    {
        public bool Equals((int, float, float) a, (int, float, float) b) =>
            a.Item1 == b.Item1 && MathF.Abs(a.Item2 - b.Item2) < 0.01f && MathF.Abs(a.Item3 - b.Item3) < 0.01f;
        public int GetHashCode((int, float, float) v) => v.Item1;
    }

    [Fact]
    public void A_builder_sent_to_a_spot_another_post_took_meanwhile_is_blocked()
    {
        var sim = NewConstructionSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(100, 0, 0))], Belt(1));
        int builder = sim.AddUnit(Blue, new Vector3(10, 0, 12), Builder);

        sim.Tick([new BuildCommand(Blue, builder, "post", new Vector3(10, 0, 2))]);
        sim.AddGatherer(Red, new Vector3(25, 0, 2), maxDistance: 3); // 15 m along: too close
        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.BuildBlocked, builder, Simulation.BlockedByTheSite), 5 * T) > 0);
        Assert.Empty(sim.State.Buildings);
    }

    [Fact]
    public void A_finished_post_becomes_a_gatherer_post_on_the_belt()
    {
        var sim = NewConstructionSim();
        sim.AddBeltLine(TwoSegments(), Belt(1));
        int builder = sim.AddUnit(Blue, new Vector3(10, 0, 4), Builder);

        sim.Tick([new BuildCommand(Blue, builder, "post", new Vector3(10, 0, 2))]);
        Run(sim, 2 * T);

        Assert.Empty(sim.State.Buildings);
        var post = Assert.Single(sim.State.Gatherers);
        Assert.Equal((Blue, new Vector3(10, 0, 2)), (post.Owner, post.Position));
        Assert.Equal(10f, post.Distance, 0.01f); // pulls from the belt beside it
        Assert.Equal(300f, post.MaxHealth);
    }

    [Fact]
    public void A_finished_defense_becomes_a_unit_that_fires_but_never_moves()
    {
        var sim = NewConstructionSim();
        int builder = sim.AddUnit(Blue, Vector3.Zero, Builder);
        sim.Tick([new BuildCommand(Blue, builder, "gunner", new Vector3(4, 0, 0))]);
        Run(sim, 2 * T);
        var gunner = sim.State.Units.Single(u => u.Type == "gunner");
        Assert.Empty(sim.State.Buildings);

        int red = sim.AddUnit(Red, new Vector3(4, 0, 9), maxHealth: 1000, dps: 0);
        sim.Tick([new MoveCommand(Blue, gunner.Id, new Vector3(30, 0, 0))]);
        Run(sim, T);

        Assert.Equal(new Vector3(4, 0, 0), UnitById(sim, gunner.Id).Position);
        Assert.True(UnitById(sim, red).Health < 1000);
    }

    [Fact]
    public void Only_builders_build_and_only_what_their_type_can()
    {
        var sim = NewConstructionSim();
        int squad = sim.AddUnit(Blue, Vector3.Zero, Squad);
        int builder = sim.AddUnit(Blue, new Vector3(0, 0, 5), Builder);

        sim.Tick([new BuildCommand(Blue, squad, "barracks", new Vector3(6, 0, 0)), new BuildCommand(Blue, builder, "hq", new Vector3(6, 0, 8))]);
        Run(sim, 3 * T);

        Assert.Empty(sim.State.Buildings);
    }

    [Fact]
    public void Buildings_snap_so_their_edges_sit_on_the_grid()
    {
        Assert.Equal(new Vector3(4, 0, 0), Simulation.SnapToGrid(new Vector3(3.3f, 0, -0.9f), 4)); // 2 cells: center on a grid line
        Assert.Equal(new Vector3(3, 0, -1), Simulation.SnapToGrid(new Vector3(3.3f, 0, -0.9f), 6)); // 3 cells: on a cell's middle
    }
}
