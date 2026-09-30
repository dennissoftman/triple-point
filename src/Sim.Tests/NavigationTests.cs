using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

public class NavGridTests
{
    // A 40 x 40 m grid centered on the origin.
    static NavGrid Grid() => new(-20, -20, 20, 20);

    [Fact]
    public void Clearance_is_the_distance_to_solid_ground_and_negative_inside_it()
    {
        var grid = Grid();
        grid.Block(Vector3.Zero, 2, 2); // x and z from -2 to 2
        grid.Finish();

        Assert.Equal(0.5f, grid.Clearance(new(2.5f, 0, 0.5f)), 3);  // the cell next to it
        Assert.Equal(2.5f, grid.Clearance(new(4.5f, 0, 0.5f)), 3);
        Assert.True(grid.Clearance(new(0.5f, 0, 0.5f)) < -1);         // deep inside
        Assert.Equal(0.5f, grid.Clearance(new(19.5f, 0, 10.5f)), 3); // against the map's edge
        Assert.True(grid.Clearance(new(25, 0, 0)) < 0);               // beyond it
        Assert.True(grid.IsSolid(new(1, 0, 1)));
        Assert.False(grid.IsSolid(new(3, 0, 1)));
    }

    [Fact]
    public void A_clear_way_is_one_straight_leg()
    {
        var grid = Grid();
        var path = new List<Vector3>();
        Assert.True(grid.FindPath(new(-10, 0, -10), new(10, 0, 10), 1, path, out int searched));
        Assert.Equal([new Vector3(10, 0, 10)], path);
        Assert.Equal(0, searched);
    }

    [Fact]
    public void A_path_goes_round_a_wall_keeping_the_units_radius_from_it()
    {
        var grid = Grid();
        grid.Block(Vector3.Zero, 1, 12); // a wall across the middle, z from -12 to 12
        grid.Finish();
        var path = new List<Vector3>();
        var from = new Vector3(-10, 0, 0);
        Assert.True(grid.FindPath(from, new(10, 0, 0), 1.5f, path, out _));

        Assert.Equal(new Vector3(10, 0, 0), path[^1]);
        Assert.True(path.Count >= 2, "it turns a corner");
        var at = from;
        foreach (var p in path)
        {
            Assert.True(grid.LineClear(at, p, 1.5f), $"leg {at} -> {p}");
            at = p;
        }
        Assert.Contains(path, p => MathF.Abs(p.Z) > 12 + 1.5f - 0.01f); // round an end of the wall
    }

    [Fact]
    public void Only_units_that_fit_go_through_a_gap()
    {
        var grid = Grid();
        // A wall from edge to edge along x = 0, with a 3 m gap at z 0..3.
        grid.Block(new(0, 0, -10), 1, 10);
        grid.Block(new(0, 0, 11.5f), 1, 8.5f);
        grid.Finish();
        var path = new List<Vector3>();
        var goal = new Vector3(10, 0, 0);

        Assert.True(grid.FindPath(new(-10, 0, 0), goal, 1f, path, out _));
        Assert.Equal(goal, path[^1]);

        Assert.True(grid.FindPath(new(-10, 0, 0), goal, 1.6f, path, out _));
        Assert.True(path[^1].X < 0, "too big for the gap: it gets as close as it can on its own side");
    }

    [Fact]
    public void A_goal_inside_something_solid_becomes_the_nearest_spot_beside_it_on_the_near_side()
    {
        var grid = Grid();
        grid.Block(Vector3.Zero, 3, 3);
        grid.Finish();
        var path = new List<Vector3>();
        Assert.True(grid.FindPath(new(-12, 0, 0), Vector3.Zero, 1, path, out _));
        var end = path[^1];
        Assert.True(grid.Clearance(end) >= 1);
        Assert.True(end.X < -3, $"on the side it came from, got {end}");
        Assert.True(Vector3.Distance(end, Vector3.Zero) < 6);
    }
}

public class NavigationTests
{
    static readonly BuildingType Block8 = new(Health: 500, Size: 8, Id: "block");

    static Simulation NavSim()
    {
        var sim = NewSim();
        sim.EnableNavigation(-40, -40, 40, 40);
        return sim;
    }

    static int Soldier(Simulation sim, Vector3 at, int owner = Blue) => sim.AddUnit(owner, at, speed: 5, radius: 1);

    static int Tank(Simulation sim, Vector3 at, int owner = Blue) =>
        sim.AddUnit(owner, at, speed: 4.5f, movement: Movement.Tracked, acceleration: 2, turnRate: 60, reverseSpeed: 2,
            easeIn: 0.5f, easeOut: 0.7f, radius: 1.5f, dps: 0);

    // Plays until the unit's order is done, checking every tick that it stays out of the building; the ticks taken.
    static int Drive(Simulation sim, int id, Vector3 target, int maxTicks, Vector3? building = null, float half = 4)
    {
        sim.Tick([new MoveCommand(Blue, id, target)]);
        for (int t = 1; t <= maxTicks; t++)
        {
            var u = UnitById(sim, id);
            if (building is Vector3 b)
                Assert.False(MathF.Abs(u.Position.X - b.X) < half && MathF.Abs(u.Position.Z - b.Z) < half, $"inside the building at tick {t}: {u.Position}");
            if (u.Current.Kind == UnitOrder.None) return t;
            sim.Tick(NoCommands);
        }
        return -1;
    }

    [Fact]
    public void Infantry_walks_round_a_building_in_its_way()
    {
        var sim = NavSim();
        sim.AddBuilding(Red, Vector3.Zero, Block8);
        int id = Soldier(sim, new(-12, 0, 0));
        int ticks = Drive(sim, id, new(12, 0, 0), 20 * T, Vector3.Zero);
        Assert.InRange(ticks, 1, 20 * T);
        Assert.True(Vector3.Distance(UnitById(sim, id).Position, new(12, 0, 0)) < 0.5f);
    }

    [Fact]
    public void A_tank_drives_round_a_building_in_its_way()
    {
        var sim = NavSim();
        sim.AddBuilding(Red, Vector3.Zero, Block8);
        int id = Tank(sim, new(-14, 0, 0));
        int ticks = Drive(sim, id, new(14, 0, 0), 40 * T, Vector3.Zero);
        Assert.InRange(ticks, 1, 40 * T);
        Assert.True(Vector3.Distance(UnitById(sim, id).Position, new(14, 0, 0)) < 2);
    }

    [Fact]
    public void A_move_into_a_building_ends_beside_it()
    {
        var sim = NavSim();
        sim.AddBuilding(Red, Vector3.Zero, Block8);
        int id = Soldier(sim, new(-12, 0, 0));
        Assert.InRange(Drive(sim, id, Vector3.Zero, 20 * T, Vector3.Zero), 1, 20 * T);
        Assert.InRange(UnitById(sim, id).Position.X, -7, -4);
    }

    [Fact]
    public void Units_on_top_of_each_other_push_apart()
    {
        var sim = NavSim();
        int a = Soldier(sim, Vector3.Zero), b = Soldier(sim, Vector3.Zero), c = Soldier(sim, new(0.3f, 0, 0));
        Run(sim, 2 * T);
        var p = new[] { a, b, c }.Select(id => UnitById(sim, id).Position).ToArray();
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                Assert.True(Vector3.Distance(p[i], p[j]) > 1.9f, $"{i} and {j} still overlap: {Vector3.Distance(p[i], p[j])}");
    }

    [Fact]
    public void Units_without_navigation_still_stack()
    {
        var sim = NewSim();
        int a = Soldier(sim, Vector3.Zero), b = Soldier(sim, Vector3.Zero);
        Run(sim, T);
        Assert.Equal(UnitById(sim, a).Position, UnitById(sim, b).Position);
    }

    [Fact]
    public void An_idle_unit_gives_way_to_one_going_past()
    {
        var sim = NavSim();
        int idle = Soldier(sim, Vector3.Zero);
        int mover = Tank(sim, new(-12, 0, 0));
        sim.Tick([new MoveCommand(Blue, mover, new(12, 0, 0))]);
        float aside = 0;
        for (int t = 0; t < 20 * T && UnitById(sim, mover).Current.Kind != UnitOrder.None; t++)
        {
            sim.Tick(NoCommands);
            aside = MathF.Max(aside, MathF.Abs(UnitById(sim, idle).Position.Z));
        }
        Assert.Equal(UnitOrder.None, UnitById(sim, mover).Current.Kind); // it got past
        Assert.True(aside > 1, "shoved aside, not ahead");

        Run(sim, 3 * T); // then back to its spot
        Assert.True(Vector3.Distance(UnitById(sim, idle).Position, Vector3.Zero) < 0.5f, $"{UnitById(sim, idle).Position}");
    }

    [Fact]
    public void A_crowd_sent_to_one_point_all_arrive()
    {
        var sim = NavSim();
        var ids = Enumerable.Range(0, 8).Select(i => Soldier(sim, new(-15 + i % 4 * 2.5f, 0, -3 + i / 4 * 2.5f))).ToArray();
        sim.Tick(ids.Select(id => (Command)new MoveCommand(Blue, id, new(15, 0, 0))).ToArray());
        Run(sim, 15 * T);
        Assert.All(ids, id => Assert.Equal(UnitOrder.None, UnitById(sim, id).Current.Kind));
        Assert.All(ids, id => Assert.True(Vector3.Distance(UnitById(sim, id).Position, new(15, 0, 0)) < 8));
    }

    [Fact]
    public void A_building_put_up_on_units_pushes_them_out()
    {
        var sim = NavSim();
        int a = Soldier(sim, new(1, 0, 1)), t = Tank(sim, new(-2, 0, 0));
        sim.AddBuilding(Blue, Vector3.Zero, Block8);
        Run(sim, 3 * T);
        foreach (int id in new[] { a, t })
        {
            var u = UnitById(sim, id);
            Assert.True(MathF.Max(MathF.Abs(u.Position.X), MathF.Abs(u.Position.Z)) >= 4 + u.Radius - 0.6f, $"{id} still in it: {u.Position}");
        }
    }

    [Fact]
    public void A_path_is_planned_again_when_a_building_goes_up_across_it()
    {
        var sim = NavSim();
        int id = Soldier(sim, new(-15, 0, 0));
        sim.Tick([new MoveCommand(Blue, id, new(15, 0, 0))]);
        Run(sim, T); // on its way, in a straight line
        sim.AddBuilding(Red, Vector3.Zero, Block8);
        for (int t = 0; t < 20 * T && UnitById(sim, id).Current.Kind != UnitOrder.None; t++)
        {
            var p = UnitById(sim, id).Position;
            Assert.False(MathF.Abs(p.X) < 4 && MathF.Abs(p.Z) < 4, $"walked into it: {p}");
            sim.Tick(NoCommands);
        }
        Assert.True(Vector3.Distance(UnitById(sim, id).Position, new(15, 0, 0)) < 0.5f);
    }

    [Fact]
    public void Nothing_leaves_the_map()
    {
        var sim = NavSim();
        int id = Soldier(sim, new(30, 0, 0));
        Drive(sim, id, new(60, 0, 0), 20 * T);
        Assert.InRange(UnitById(sim, id).Position.X, 30, 40 - 1 + 0.01f);
    }

    [Fact]
    public void Many_units_ordered_at_once_all_get_paths_in_time()
    {
        var sim = NavSim();
        sim.AddBuilding(Red, Vector3.Zero, new BuildingType(Health: 500, Size: 30, Id: "wall"));
        var ids = Enumerable.Range(0, 40).Select(i => Soldier(sim, new(-30 + i % 5 * 2.5f, 0, -12 + i / 5 * 2.5f))).ToArray();
        sim.Tick(ids.Select((id, i) => (Command)new MoveCommand(Blue, id, new(25 + i % 5 * 2.5f, 0, -12 + i / 5 * 2.5f))).ToArray());
        Run(sim, 30 * T);
        Assert.All(ids, id => Assert.Equal(UnitOrder.None, UnitById(sim, id).Current.Kind));
        Assert.All(ids, id => Assert.True(UnitById(sim, id).Position.X > 18, $"{UnitById(sim, id).Position}"));
    }

    [Fact]
    public void Nothing_is_built_on_an_obstacle_or_off_the_map()
    {
        var sim = NavSim();
        sim.AddObstacle(new(10, 0, 10), 3, 1, MathF.PI / 4); // 6 m long along (1, -1), 2 m thick along (1, 1)
        var hut = new BuildingType(Health: 100, Size: 4, Id: "hut");
        Assert.False(sim.CanPlace(hut, new(10, 0, 10)));
        Assert.False(sim.CanPlace(hut, new(14, 0, 6)));   // along its length
        Assert.False(sim.CanPlace(hut, new(12, 0, 12)));  // a corner of the hut reaches its side
        Assert.True(sim.CanPlace(hut, new(14, 0, 14)));   // clear of its side
        Assert.False(sim.CanPlace(hut, new(39, 0, 0)));   // over the edge
        Assert.True(sim.CanPlace(hut, new(37, 0, 0)));
    }

    [Fact]
    public void Units_walk_round_obstacles()
    {
        var sim = NavSim();
        sim.AddObstacle(Vector3.Zero, 1, 10);
        int id = Soldier(sim, new(-10, 0, 0));
        Assert.InRange(Drive(sim, id, new(10, 0, 0), 20 * T, Vector3.Zero, half: 1), 1, 20 * T);
    }
}

public class MainMapNavigationTests
{
    [Fact]
    public void The_main_map_has_mirrored_rocks_that_block_and_bases_with_room_to_leave()
    {
        var sim = MainMap.Load();
        Run(sim, 1);
        var rocks = sim.State.Obstacles;
        Assert.Equal(6, rocks.Count);
        Assert.All(rocks, r => Assert.True(sim.Nav!.IsSolid(r.Position)));
        Assert.All(rocks, r => Assert.Contains(rocks, m => Vector3.Distance(m.Position, -r.Position) < 0.01f)); // point-symmetric

        // A tank-sized path from each HQ's door to the middle of the map.
        var path = new List<Vector3>();
        foreach (var hq in sim.State.Buildings)
        {
            Assert.True(sim.Nav!.FindPath(hq.Exit, Vector3.Zero, 1.5f, path, out _));
            Assert.True(Vector3.Distance(path[^1], Vector3.Zero) < 3, $"from {hq.Exit} it gets to {path[^1]}");
        }
    }

    [Fact]
    public void A_new_order_drops_the_path_to_the_old_ones_target()
    {
        var sim = NewSim();
        sim.EnableNavigation(-40, -40, 40, 40);
        int unit = sim.AddUnit(Blue, Vector3.Zero, speed: 5, dps: 0, range: 8);
        int far = sim.AddUnit(Red, new Vector3(35, 0, 0), speed: 0, maxHealth: 1000, dps: 0);
        int near = sim.AddUnit(Red, new Vector3(-1, 0, -6), speed: 0, maxHealth: 1000, dps: 0);
        sim.Tick([new AttackCommand(Blue, unit, far)]);
        Run(sim, T / 2);
        Assert.NotNull(sim.PathOf(UnitById(sim, unit), out _));

        // Not queued: the attack on the near one, already in range, replaces it, so it doesn't move and has
        // no path; the way to the far one mustn't linger (the view draws it as a waypoint).
        sim.Tick([new AttackCommand(Blue, unit, near)]);
        Run(sim, 2);
        Assert.Equal(near, UnitById(sim, unit).Current.TargetId);
        Assert.Null(sim.PathOf(UnitById(sim, unit), out _));
    }
}
