using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Artillery: fires only standing still, not too close, ballistic scattered shells, splash that breaks belt.</summary>
public class ArtilleryTests
{
    static readonly WeaponType Gun = new(WeaponKind.Shell, Damage: 30, Reload: 5, Range: 28, ShellSpeed: 22,
        Hit: HitKind.Splash, SplashRadius: 3.5f, MinRange: 8, Ballistic: true, Scatter: 3);
    static readonly UnitType Artillery = new(Members: 1, Speed: 3, MemberHealth: 120, Weapon: "gun", Movement: Movement.Tracked,
        Acceleration: 1.5f, TurnRate: 45, TurretTurnRate: 40, TurretArc: 30, StopsToFire: true, Id: "artillery") { Gun = Gun };

    static int ShotsFired(Simulation sim, int ticks)
    {
        int shells = 0;
        for (int i = 0; i < ticks; i++)
        {
            int before = sim.State.Projectiles.Count;
            sim.Tick(NoCommands);
            if (sim.State.Projectiles.Count > before) shells++;
        }
        return shells;
    }

    [Fact]
    public void It_holds_fire_on_the_move_and_fires_once_stopped()
    {
        var sim = NewSim();
        int gun = sim.AddUnit(Blue, Vector3.Zero, Artillery);
        sim.AddUnit(Red, new Vector3(20, 0, 0), speed: 0, maxHealth: 1000, dps: 0);

        sim.Tick([new MoveCommand(Blue, gun, new Vector3(0, 0, 30))]); // drives past, the target in range all along
        Assert.Equal(0, ShotsFired(sim, 3 * T));

        sim.Tick([new MoveCommand(Blue, gun, UnitById(sim, gun).Position)]);
        Assert.True(ShotsFired(sim, 8 * T) > 0);
    }

    [Fact]
    public void Ordered_to_attack_it_closes_a_little_inside_its_range_and_a_small_step_back_does_not_move_it()
    {
        var sim = NewSim();
        int gun = sim.AddUnit(Blue, Vector3.Zero, Artillery);
        int red = sim.AddUnit(Red, new Vector3(0, 0, 40), speed: 2, maxHealth: 10000, dps: 0);

        sim.Tick([new AttackCommand(Blue, gun, red)]);
        for (int i = 0; i < 20 * T && ShotsFired(sim, 1) == 0; i++) { }
        float stoppedAt = Vector3.Distance(UnitById(sim, gun).Position, UnitById(sim, red).Position);
        Assert.InRange(stoppedAt, 20f, 28f - 1.5f); // came in past the outer ring

        var from = UnitById(sim, gun).Position;
        sim.Tick([new MoveCommand(Red, red, UnitById(sim, red).Position + new Vector3(0, 0, 1.5f))]); // steps back a little
        Run(sim, 3 * T);
        Assert.Equal(from, UnitById(sim, gun).Position); // still in range: it stays put
    }

    [Fact]
    public void Its_turret_only_reaches_so_far_so_it_swings_the_hull_round_to_a_target_behind()
    {
        var sim = NewSim();
        int gun = sim.AddUnit(Blue, Vector3.Zero, Artillery); // facing +z
        sim.AddUnit(Red, new Vector3(0, 0, -20), speed: 0, maxHealth: 1000, dps: 0); // right behind it

        float arc = MathF.PI / 6;
        for (int i = 0; i < 2 * T; i++)
        {
            sim.Tick(NoCommands);
            var u = UnitById(sim, gun);
            float off = MathF.Abs(MathF.IEEERemainder(u.Turret - u.Heading, MathF.Tau));
            Assert.True(off <= arc + 1e-3f, $"turret {off} rad off the nose");
        }
        Assert.True(ShotsFired(sim, 8 * T) > 0); // 180° at 45°/s, less the arc: it came round and fired
        Assert.Equal(Vector3.Zero, UnitById(sim, gun).Position); // pivoted where it stood
    }

    [Fact]
    public void It_holds_its_ground_when_shot_from_beyond_its_reach()
    {
        var sim = NewSim();
        int gun = sim.AddUnit(Blue, Vector3.Zero, Artillery);
        sim.AddUnit(Red, new Vector3(0, 0, 40), speed: 0, maxHealth: 1000, dps: 5, range: 45); // outranges it

        Run(sim, 4 * T);
        Assert.True(UnitById(sim, gun).Health < 120);
        Assert.Equal(Vector3.Zero, UnitById(sim, gun).Position); // no chasing
    }

    [Fact]
    public void It_ignores_what_is_inside_its_minimum_range()
    {
        var sim = NewSim();
        sim.AddUnit(Blue, Vector3.Zero, Artillery);
        sim.AddUnit(Red, new Vector3(5, 0, 0), speed: 0, maxHealth: 1000, dps: 0); // too close
        Assert.Equal(0, ShotsFired(sim, 6 * T));

        sim.AddUnit(Red, new Vector3(-15, 0, 0), speed: 0, maxHealth: 1000, dps: 0); // in the ring
        Assert.True(ShotsFired(sim, 8 * T) > 0);
    }

    [Fact]
    public void A_ballistic_shell_lands_where_it_was_aimed_give_or_take_the_scatter_not_where_the_target_went()
    {
        var sim = NewSim();
        sim.AddUnit(Blue, Vector3.Zero, Artillery);
        int red = sim.AddUnit(Red, new Vector3(0, 0, 28), speed: 6, maxHealth: 1000, dps: 0); // full range: full scatter

        for (int i = 0; i < 6 * T && sim.State.Projectiles.Count == 0; i++) sim.Tick(NoCommands);
        var shell = Assert.Single(sim.State.Projectiles);
        Assert.True(shell.Ballistic);
        Assert.InRange(Vector2.Distance(new(shell.Target.X, shell.Target.Z), new(0, 28)), 0, 3.01f);

        var aimed = shell.Target;
        sim.Tick([new MoveCommand(Red, red, new Vector3(20, 0, 28))]); // runs; the shell doesn't follow
        for (int i = 0; i < 3 * T && sim.State.Projectiles.Count > 0; i++) sim.Tick(NoCommands);
        Assert.Empty(sim.State.Projectiles);
        Assert.Equal(aimed, shell.Target);
        Assert.Equal(1000f, UnitById(sim, red).Health); // it got away
    }

    [Fact]
    public void Splash_breaks_belt_anyone_s_but_not_covered_belt()
    {
        var sim = NewSim();
        // An open segment and a covered one, side by side along the line; the target stands between them.
        sim.AddBeltLine([Straight(new(-10, 0, 20), new(0, 0, 20)), Straight(new(0, 0, 20), new(10, 0, 20))], Belt(1), coveredEnd: 10);
        var segments = sim.State.Belts[0].Segments;
        sim.AddUnit(Blue, Vector3.Zero, Artillery with { Gun = Gun with { Scatter = 0 } });
        sim.AddUnit(Red, new Vector3(0, 0, 21), speed: 0, maxHealth: 10000, dps: 0);

        Run(sim, 30 * T); // a shell every 5 s, each hitting both segments' ends near the blast
        Assert.True(segments[0].Health < segments[0].MaxHealth);
        Assert.Equal(segments[1].MaxHealth, segments[1].Health);
    }

    [Fact]
    public void Engineers_repair_quicker_and_cheaper_than_builders_but_do_not_build()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1));
        sim.State.Players[Blue].Packages = 10;
        int engineer = sim.AddUnit(Blue, new Vector3(15, 0, 2), speed: 5, repairSeconds: 2.5f, repairCost: 2); // repairs, doesn't build

        sim.Tick([new BreakSegmentCommand(0, 1), new RepairSegmentCommand(Blue, engineer, 0, 1)]);
        int repairedAt = TicksUntil(sim, new SimEvent(SimEventKind.SegmentRepaired, 0, 1), 5 * T);
        Assert.InRange(repairedAt, 48, 51); // 2.5 s of work (50 ticks)
        Assert.Equal(8, sim.State.Players[Blue].Packages);

        sim.BuildingTypes = new Dictionary<string, BuildingType> { ["post"] = new(Health: 100, Size: 2, Cost: 1, BuildTime: 1, Kind: BuildingKind.Post, Id: "post") };
        sim.Tick([new BuildCommand(Blue, engineer, "post", new Vector3(5, 0, 2))]);
        Run(sim, 3 * T);
        Assert.Empty(sim.State.Buildings);
    }
}
