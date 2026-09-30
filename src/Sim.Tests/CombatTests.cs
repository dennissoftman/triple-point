using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>Ownership, fighting, and squads.</summary>
public class CombatTests
{
    [Fact]
    public void Orders_to_another_players_units_are_ignored()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero);

        sim.Tick([new MoveCommand(Red, blue, new Vector3(10, 0, 0))]);
        Run(sim, 20);

        Assert.Equal(Vector3.Zero, UnitById(sim, blue).Position);
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind);
    }

    [Fact]
    public void Attack_chases_into_range_and_destroys_the_target()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(20, 0, 0), dps: 0); // unarmed, so it can't fight back

        // 12 m to get within 8 m (48 ticks), then 100 health at 10 dps (200 ticks).
        int diedAt = TicksUntil(sim, new SimEvent(SimEventKind.UnitDied, red), 400, [new AttackCommand(Blue, blue, red)]);

        Assert.InRange(diedAt, 247, 251);
        Assert.DoesNotContain(sim.State.Units, u => u.Id == red);
        sim.Tick(NoCommands); // shots land after every unit has acted, so the order ends the tick after
        Assert.Equal(UnitOrder.None, UnitById(sim, blue).Current.Kind);
        Assert.Equal(12f, UnitById(sim, blue).Position.X, 0.3f); // fired from range, didn't walk up to it
    }

    [Fact]
    public void Units_cannot_be_ordered_to_attack_their_own_side()
    {
        var sim = NewSim();
        int a = sim.AddUnit(Blue, Vector3.Zero);
        int b = sim.AddUnit(Blue, new Vector3(3, 0, 0));

        sim.Tick([new AttackCommand(Blue, a, b)]);
        Run(sim, 20);

        Assert.Equal(100f, UnitById(sim, b).Health);
        Assert.Equal(UnitOrder.None, UnitById(sim, a).Current.Kind);
    }

    [Fact]
    public void Moving_units_fire_on_the_move_without_stopping()
    {
        var sim = NewSim();
        int red = sim.AddUnit(Red, Vector3.Zero, speed: 0, dps: 10, range: 8); // can't move, so it doesn't chase Blue off (return fire)
        int blue = sim.AddUnit(Blue, new Vector3(-10, 0, 5), speed: 5, dps: 10, range: 8);

        // Blue walks past Red, 5 m away at the closest; each is inside the other's 8 m range for ~12.5 m
        // (2.5 s), and they trade fire the whole time.
        int arrived = TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, blue), 6 * T,
            [new MoveCommand(Blue, blue, new Vector3(10, 0, 5))]);
        Run(sim, 2 * T);

        Assert.InRange(arrived, 79, 81); // 20 m at 5 m/s: shooting didn't slow it down
        Assert.InRange(UnitById(sim, blue).Health, 72f, 78f);
        Assert.InRange(UnitById(sim, red).Health, 72f, 78f); // shot back while walking
        Assert.False(UnitById(sim, red).Firing);             // out of range again
    }

    [Fact]
    public void Squad_loses_members_and_firepower_as_its_health_drops()
    {
        var sim = NewSim();
        int squad = sim.AddUnit(Red, Vector3.Zero, maxHealth: 100, dps: 10, members: 5);
        int tank = sim.AddUnit(Blue, new Vector3(5, 0, 0), maxHealth: 1000, dps: 20);

        Assert.Equal(5, UnitById(sim, squad).Members);
        Assert.Equal(10f, UnitById(sim, squad).Dps);

        sim.Tick([new AttackCommand(Blue, tank, squad)]);
        Run(sim, T - 1); // 1 s at 20 dps: one member's 20 health

        var hit = UnitById(sim, squad);
        Assert.Equal(80f, hit.Health, 0.01f);
        Assert.Equal(4, hit.Members);
        Assert.Equal(8f, hit.Dps, 0.01f); // each lost member takes its 2 dps with it
    }

    [Fact]
    public void Destroyed_gatherer_is_removed_and_its_owner_stops_earning()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(0.5f));
        int post = sim.AddGatherer(Blue, new Vector3(5, 0, 2), maxDistance: 3);
        int raider = sim.AddUnit(Red, new Vector3(5, 0, 8), dps: 10); // 6 m away, in range

        int destroyedAt = TicksUntil(sim, new SimEvent(SimEventKind.GathererDestroyed, post), 1000,
            [new AttackCommand(Red, raider, post)]);
        Assert.InRange(destroyedAt, 599, 602); // 300 health at 10 dps
        Assert.Empty(sim.State.Gatherers);

        int earned = sim.State.Players[Blue].Gathered;
        Assert.True(earned > 0);
        Run(sim, 20 * T);
        Assert.Equal(earned, sim.State.Players[Blue].Gathered);
    }

    [Fact]
    public void Posts_pay_their_owner_and_pickups_pay_whoever_collects_them()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(0.5f));
        sim.AddGatherer(Blue, new Vector3(3, 0, 2), maxDistance: 3);
        // Red stands on both sides of a break further down and scoops the spill.
        sim.AddUnit(Red, new Vector3(10, 0, 1.9f), dps: 0);
        sim.AddUnit(Red, new Vector3(10, 0, -1.9f), dps: 0);

        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 30 * T);

        var (blue, red) = (sim.State.Players[Blue], sim.State.Players[Red]);
        Assert.True(blue.Gathered > 0);
        Assert.Equal(0, blue.Collected);
        Assert.True(red.Collected > 0);
        Assert.Equal(0, red.Gathered);
        Assert.Equal(red.Collected, red.Packages);
    }

    [Fact]
    public void Idle_units_focus_the_weakest_enemy_in_range()
    {
        var sim = NewSim();
        sim.AddUnit(Blue, Vector3.Zero, dps: 10);
        sim.AddUnit(Blue, new Vector3(0, 0, 2), dps: 10);
        int healthy = sim.AddUnit(Red, new Vector3(3, 0, 0), maxHealth: 100, dps: 0); // nearer, full health
        int weak = sim.AddUnit(Red, new Vector3(6, 0, 0), maxHealth: 40, dps: 0);     // farther, but dies sooner

        Run(sim, T); // both Blue units put 1 s into the same target

        Assert.Equal(100f, UnitById(sim, healthy).Health);
        Assert.Equal(20f, UnitById(sim, weak).Health, 0.01f);
        Run(sim, T + 1); // the weak one dies, then they move on to the other
        Assert.DoesNotContain(sim.State.Units, u => u.Id == weak);
        Assert.True(UnitById(sim, healthy).Health < 100f);
    }

    [Fact]
    public void Tracked_vehicle_turns_nearly_on_the_spot_before_driving_off()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 3, turnRate: 60);

        // Straight behind it: it has to turn 180 degrees at 60 degrees a second.
        sim.Tick([new MoveCommand(Blue, tank, new Vector3(0, 0, -10))]);
        Run(sim, T - 1);
        var turning = UnitById(sim, tank);
        Assert.Equal(MathF.PI / 3, MathF.Abs(turning.Heading), 0.02f);
        Assert.True(turning.Position.Length() < 1f, $"moved {turning.Position.Length()} m while pivoting");

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, tank), 20 * T) > 0);
        Run(sim, T); // coast to a halt
        var parked = UnitById(sim, tank);
        Assert.True(Vector3.Distance(parked.Position, new Vector3(0, 0, -10)) < 1f);
        Assert.Equal(0f, parked.CurrentSpeed);
    }

    [Fact]
    public void Wheeled_vehicle_needs_speed_to_steer_so_it_arcs_around()
    {
        var sim = NewSim();
        int car = sim.AddUnit(Blue, Vector3.Zero, speed: 8, movement: Movement.Wheeled, acceleration: 6, turnRate: 120);

        sim.Tick([new MoveCommand(Blue, car, new Vector3(0, 0, -10))]);
        Run(sim, T - 1);
        var turning = UnitById(sim, car);
        Assert.True(turning.Position.Length() > 1.5f, $"only moved {turning.Position.Length()} m: it can't turn in place");
        Assert.True(MathF.Abs(turning.Heading) < MathF.PI * 0.9f); // still coming around

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, car), 20 * T) > 0);
    }

    [Fact]
    public void Stopped_vehicle_backs_up_to_a_close_target_behind_it()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 3, turnRate: 60, reverseSpeed: 2.2f);

        sim.Tick([new MoveCommand(Blue, tank, new Vector3(0, 0, -6))]); // 6 m straight behind
        Run(sim, T - 1);
        var backing = UnitById(sim, tank);
        Assert.True(backing.CurrentSpeed < 0);
        Assert.Equal(0f, backing.Heading, 0.01f); // still facing the way it was
        Assert.True(backing.Position.Z < -0.5f);

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, tank), 10 * T) > 0);
        Run(sim, T);
        Assert.True(Vector3.Distance(UnitById(sim, tank).Position, new Vector3(0, 0, -6)) < 1f);
    }

    [Fact]
    public void Vehicle_turns_around_for_a_far_target_even_if_it_can_back_up()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 3, turnRate: 60, reverseSpeed: 2.2f);

        sim.Tick([new MoveCommand(Blue, tank, new Vector3(0, 0, -30))]);
        Run(sim, T - 1);

        var turning = UnitById(sim, tank);
        Assert.True(turning.CurrentSpeed >= 0);
        Assert.Equal(MathF.PI / 3, MathF.Abs(turning.Heading), 0.02f);
    }

    [Fact]
    public void Attack_move_stops_to_fight_on_the_way_then_carries_on()
    {
        var sim = NewSim();
        int blue = sim.AddUnit(Blue, Vector3.Zero, speed: 5, dps: 10, range: 8);
        int red = sim.AddUnit(Red, new Vector3(15, 0, 3), dps: 0); // off to the side of the route

        // Walks until Red is 8 m away (x = 15 - sqrt(8² - 3²) ≈ 7.6), fights 10 s, then walks on.
        int killedAt = TicksUntil(sim, new SimEvent(SimEventKind.UnitDied, red), 30 * T,
            [new AttackMoveCommand(Blue, blue, new Vector3(30, 0, 0))]);
        Assert.True(killedAt > 0);
        Assert.Equal(7.6f, UnitById(sim, blue).Position.X, 0.3f);

        Assert.True(TicksUntil(sim, new SimEvent(SimEventKind.UnitArrived, blue), 10 * T) > 0);
        Assert.Equal(new Vector3(30, 0, 0), UnitById(sim, blue).Position);
    }

    [Fact]
    public void Vehicles_accelerate_instead_of_starting_at_full_speed()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 3, turnRate: 60);

        sim.Tick([new MoveCommand(Blue, tank, new Vector3(0, 0, 30))]); // straight ahead
        Run(sim, 9); // 0.5 s
        Assert.Equal(1.5f, UnitById(sim, tank).CurrentSpeed, 0.01f);
        Run(sim, 2 * T);
        Assert.Equal(4.5f, UnitById(sim, tank).CurrentSpeed, 0.01f);
    }

    [Fact]
    public void Eased_vehicle_speeds_up_and_slows_down_gently_at_both_ends()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 2, braking: 4,
            easeIn: 0.5f, easeOut: 0.5f, turnRate: 60);
        var target = new Vector3(0, 0, 30); // straight ahead

        var speeds = new List<float> { 0 };
        var z = new List<float>();
        sim.Tick([new MoveCommand(Blue, tank, target)]);
        for (int i = 0; i < 20 * T; i++)
        {
            var now = UnitById(sim, tank);
            speeds.Add(now.CurrentSpeed);
            z.Add(now.Position.Z);
            sim.Tick(NoCommands);
        }
        var changes = speeds.Zip(speeds.Skip(1), (a, b) => b - a).ToList(); // m/s per tick

        // Pulling away, the push builds over 0.5 s to the full 2 m/s² (0.1 m/s per tick)...
        Assert.True(changes[0] < 0.02f && changes[4] > changes[0] && changes[9] > 0.09f, string.Join(", ", changes.Take(12)));
        // ...then fades as it reaches top speed, which it never passes.
        int top = speeds.IndexOf(4.5f);
        Assert.True(top > 0, $"never reached top speed: {speeds.Max()}");
        Assert.True(changes[top - 1] < 0.03f && speeds.Max() <= 4.5f);

        // Braking builds up and fades too: never harder than 4 m/s² (0.2 m/s per tick), gentle at the end,
        // and it comes to rest just short of the target instead of overshooting.
        int rest = speeds.FindIndex(top, s => s == 0);
        Assert.True(rest > 0, "never came to rest");
        Assert.True(changes.Min() >= -0.2f - 1e-4f && changes[rest - 1] > -0.05f, string.Join(", ", changes.Skip(rest - 12).Take(12)));
        Assert.True(30 - z[^1] < 0.6f, $"stopped {30 - z[^1]} m short");
        Assert.True(z.Max() <= 30.1f, $"overshot to {z.Max()}");
    }

    [Fact]
    public void Eased_turn_builds_up_and_settles_on_the_heading_without_overshooting()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, movement: Movement.Tracked, acceleration: 2,
            easeIn: 0.5f, easeOut: 0.5f, turnRate: 60);

        // Far off to the side: a 90 degree turn toward +x, whose bearing barely moves as the tank crawls.
        var headings = new List<float> { 0 };
        sim.Tick([new MoveCommand(Blue, tank, new Vector3(2000, 0, 0))]);
        for (int i = 0; i < 4 * T; i++)
        {
            headings.Add(UnitById(sim, tank).Heading);
            sim.Tick(NoCommands);
        }
        var steps = headings.Zip(headings.Skip(1), (a, b) => (b - a) * 180 / MathF.PI).ToList(); // degrees per tick

        Assert.True(steps[0] < 1f && steps[9] > 2.9f, string.Join(", ", steps.Take(12))); // up to 60°/s over 0.5 s
        Assert.True(steps.Max() <= 3.001f);
        Assert.All(steps, s => Assert.True(s >= -0.05f, $"turned back {s}°: it overshot")); // settles, never swings past
        Assert.Equal(90f, headings[^1] * 180 / MathF.PI, 1f);
        int settled = steps.FindIndex(s => s < 0.1f && s > -0.1f);
        Assert.True(steps[settled - 1] < 1f, "stopped turning dead instead of winding down");
    }

    [Fact]
    public void Turret_swings_onto_an_attack_target_while_driving_there_and_fires_on_arrival()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, speed: 4.5f, dps: 20, range: 14, movement: Movement.Tracked,
            acceleration: 3, turnRate: 60, turretTurnRate: 90);
        int enemy = sim.AddUnit(Red, new Vector3(30, 0, 0), maxHealth: 1000, dps: 0); // off to the side, out of range

        sim.Tick([new AttackCommand(Blue, tank, enemy)]);
        Run(sim, T - 1);
        var driving = UnitById(sim, tank);
        Assert.Equal(MathF.PI / 2, driving.Turret, 0.05f);          // on target after 1 s at 90°/s...
        Assert.True(driving.Heading < MathF.PI / 2 - 0.2f);         // ...while the hull, at 60°/s, is still coming round
        Assert.False(driving.Firing);                               // out of range: no shooting yet

        int inRange = -1, firstHit = -1;
        for (int tick = 0; tick < 20 * T && firstHit < 0; tick++)
        {
            sim.Tick(NoCommands);
            if (inRange < 0 && Vector3.Distance(UnitById(sim, tank).Position, new Vector3(30, 0, 0)) <= 14) inRange = tick;
            if (UnitById(sim, enemy).Health < 1000) firstHit = tick;
        }
        Assert.True(inRange > 0 && firstHit > 0);
        Assert.InRange(firstHit - inRange, 0, 1); // aimed already: fires as it comes into range
    }

    [Fact]
    public void Turret_has_to_come_around_before_it_fires()
    {
        var sim = NewSim();
        sim.AddUnit(Blue, Vector3.Zero, dps: 20, range: 14, movement: Movement.Tracked, acceleration: 3, turnRate: 60, turretTurnRate: 90);
        int behind = sim.AddUnit(Red, new Vector3(0, 0, -8), maxHealth: 1000, dps: 0); // in range, straight behind

        Run(sim, T); // 90 of the 180 degrees
        Assert.Equal(1000f, UnitById(sim, behind).Health);
        Run(sim, T); // round by 2 s (a little under, within the ~6° it fires at)
        Assert.True(UnitById(sim, behind).Health < 1000f);
    }

    [Fact]
    public void Turret_ignores_enemies_out_of_range_unless_ordered_to_attack()
    {
        var sim = NewSim();
        int tank = sim.AddUnit(Blue, Vector3.Zero, dps: 20, range: 10, movement: Movement.Tracked, acceleration: 3, turnRate: 60, turretTurnRate: 90);
        sim.AddUnit(Red, new Vector3(13, 0, 0), dps: 0); // just beyond its 10 m

        Run(sim, 2 * T);
        Assert.Equal(0f, UnitById(sim, tank).Turret); // idle: stays over the nose

        sim.AddUnit(Red, new Vector3(-6, 0, 0), dps: 0); // in range, the other side
        Run(sim, 2 * T);
        Assert.Equal(-MathF.PI / 2, UnitById(sim, tank).Turret, 0.05f);
    }

    [Fact]
    public void Tank_shells_fly_to_the_target_and_hit_once_per_reload()
    {
        var sim = NewSim();
        var cannon = new WeaponType(WeaponKind.Shell, Damage: 40, Reload: 3, Range: 14, ShellSpeed: 40);
        sim.AddUnit(Blue, Vector3.Zero, movement: Movement.Tracked, acceleration: 2, turnRate: 60, weapon: cannon);
        int target = sim.AddUnit(Red, new Vector3(0, 0, 12), maxHealth: 1000, dps: 0); // straight ahead, in range

        sim.Tick(NoCommands);
        Assert.Single(sim.State.Projectiles);                   // fired...
        Assert.Equal(1000f, UnitById(sim, target).Health);      // ...but nothing lands yet
        int hitAt = TicksUntil(sim, new SimEvent(SimEventKind.ShellHit, sim.State.Projectiles[0].Id), T);
        Assert.InRange(hitAt, 5, 7);                            // ~11.5 m at 40 m/s: ~0.3 s
        Assert.Equal(960f, UnitById(sim, target).Health);

        Run(sim, 3 * T - hitAt - 2);                            // just short of the reload
        Assert.Equal(960f, UnitById(sim, target).Health);
        Run(sim, 12);
        Assert.Equal(920f, UnitById(sim, target).Health);       // the second shell
    }

    [Fact]
    public void A_shell_whose_target_dies_on_the_way_lands_harmlessly()
    {
        var sim = NewSim();
        var cannon = new WeaponType(WeaponKind.Shell, Damage: 40, Reload: 3, Range: 14, ShellSpeed: 10);
        sim.AddUnit(Blue, Vector3.Zero, weapon: cannon);
        int target = sim.AddUnit(Red, new Vector3(0, 0, 12), maxHealth: 10, dps: 0);
        sim.AddUnit(Blue, new Vector3(0, 0, 8), dps: 100); // kills it long before the slow shell arrives

        Run(sim, 2 * T);

        Assert.DoesNotContain(sim.State.Units, u => u.Id == target);
        Assert.Empty(sim.State.Projectiles); // landed where the target was
    }

    [Fact]
    public void A_squad_fires_every_living_members_weapon_in_one_shot()
    {
        var sim = NewSim();
        var rifle = new WeaponType(WeaponKind.Bullet, Damage: 1, Reload: 0.5f, Range: 8);
        sim.AddUnit(Blue, Vector3.Zero, maxHealth: 100, members: 5, weapon: rifle);
        int target = sim.AddUnit(Red, new Vector3(0, 0, 5), maxHealth: 1000, dps: 0);

        sim.Tick(NoCommands);
        Assert.Equal(995f, UnitById(sim, target).Health); // 5 members x 1
        Run(sim, 9);
        Assert.Equal(995f, UnitById(sim, target).Health); // reloading for 0.5 s
        sim.Tick(NoCommands);
        Assert.Equal(990f, UnitById(sim, target).Health);
    }

    [Fact]
    public void Direct_shells_hit_only_their_target_and_splash_hits_every_enemy_around()
    {
        foreach (var hit in new[] { HitKind.Direct, HitKind.Splash })
        {
            var sim = NewSim();
            var shell = new WeaponType(WeaponKind.Shell, Damage: 40, Reload: 10, Range: 14, ShellSpeed: 40,
                Hit: hit, SplashRadius: hit == HitKind.Splash ? 3 : 0);
            sim.AddUnit(Blue, Vector3.Zero, weapon: shell);
            int target = sim.AddUnit(Red, new Vector3(0, 0, 10), maxHealth: 1000, dps: 0);
            int near = sim.AddUnit(Red, new Vector3(2, 0, 10), maxHealth: 1000, dps: 0);  // 2 m from the impact
            int far = sim.AddUnit(Red, new Vector3(5, 0, 10), maxHealth: 1000, dps: 0);   // outside 3 m
            int friend = sim.AddUnit(Blue, new Vector3(-1, 0, 10), maxHealth: 1000, dps: 0);

            Run(sim, T);

            Assert.Equal(960f, UnitById(sim, target).Health);
            Assert.Equal(hit == HitKind.Splash ? 1000 - 40 * (1 - 0.5f * 2 / 3) : 1000f, UnitById(sim, near).Health, 0.01f);
            Assert.Equal(1000f, UnitById(sim, far).Health);
            Assert.Equal(1000f, UnitById(sim, friend).Health); // no friendly fire
        }
    }

    [Fact]
    public void Splash_hurts_a_squad_by_how_much_of_it_the_blast_covers()
    {
        var sim = NewSim();
        var mortar = new WeaponType(WeaponKind.Shell, Damage: 10, Reload: 10, Range: 20, ShellSpeed: 40, Hit: HitKind.Splash, SplashRadius: 2);
        sim.AddUnit(Blue, Vector3.Zero, weapon: mortar);
        int squad = sim.AddUnit(Red, new Vector3(0, 0, 10), maxHealth: 100, dps: 0, members: 5); // right under it
        int edge = sim.AddUnit(Red, new Vector3(2, 0, 10), maxHealth: 100, dps: 0, members: 5);  // at the blast's edge: half its footprint inside

        Run(sim, T);

        Assert.Equal(100 - 10 * 5, UnitById(sim, squad).Health, 0.01f); // every member caught, full damage
        Assert.Equal(100 - 10 * 0.5f * 0.5f * 5, UnitById(sim, edge).Health, 0.01f); // half the members, at the edge's half damage
    }

    [Fact]
    public void Unit_types_load_from_json_with_their_weapons()
    {
        var weapons = GameData.ParseWeapons("""
            // comments and trailing commas are fine
            {
              "rifle": { "kind": "bullet", "damage": 1, "reload": 0.5, "range": 8 },
              "cannon": { "kind": "shell", "damage": 42, "reload": 3, "range": 14, "shellSpeed": 40 },
              "mg": { "kind": "bullet", "damage": 1.35, "reload": 0.15, "range": 12 },
            }
            """);
        var types = GameData.ParseUnitTypes("""
            {
              "rifle_squad": { "members": 5, "speed": 4.5, "memberHealth": 20, "weapon": "rifle" },
              "tank": { "members": 1, "movement": "tracked", "speed": 4.5, "acceleration": 2, "braking": 4,
                        "easeIn": 0.5, "easeOut": 0.7, "turnRate": 60, "turretTurnRate": 90, "reverseSpeed": 2.2,
                        "memberHealth": 260, "weapon": "cannon" },
              "car": { "members": 1, "movement": "wheeled", "speed": 8, "acceleration": 4, "turnRate": 120,
                       "memberHealth": 140, "weapon": "mg" },
            }
            """, weapons);
        Assert.Equal("tank", types["tank"].Id);
        Assert.Equal("cannon", types["tank"].Gun.Id);

        var sim = NewSim();
        var squad = UnitById(sim, sim.AddUnit(Blue, Vector3.Zero, types["rifle_squad"]));
        Assert.Equal(5, squad.Members);
        Assert.Equal(100f, squad.MaxHealth);
        Assert.Equal(10f, squad.Dps, 0.001f); // 1 a shot every 0.5 s, from each of 5
        Assert.Equal((WeaponKind.Bullet, 8f), (squad.WeaponKind, squad.Range));
        Assert.Equal(4.5f, squad.Speed);
        Assert.Equal(Movement.Foot, squad.Movement);

        var tank = UnitById(sim, sim.AddUnit(Blue, Vector3.Zero, types["tank"]));
        Assert.Equal((WeaponKind.Shell, 14f, 40f, 60), (tank.WeaponKind, tank.Range, tank.ShellSpeed, tank.ReloadTicks));
        Assert.Equal(Movement.Tracked, tank.Movement);
        Assert.Equal(MathF.PI / 3, tank.TurnRate, 0.001f);
        Assert.Equal(MathF.PI / 2, tank.TurretTurnRate, 0.001f);
        Assert.Equal((2f, 4f, 0.5f, 0.7f), (tank.Acceleration, tank.Braking, tank.EaseIn, tank.EaseOut));
        Assert.Equal(2.2f, tank.ReverseSpeed);
        Assert.Equal("tank", tank.Type);
        Assert.Equal("rifle_squad", squad.Type);

        var car = UnitById(sim, sim.AddUnit(Blue, Vector3.Zero, types["car"]));
        Assert.Equal((8f, 0f, 0f, 0f), (car.Braking, car.EaseIn, car.EaseOut, car.TurretTurnRate)); // braking: twice acceleration
        Assert.Equal(3, car.ReloadTicks); // 0.15 s

        Assert.Throws<InvalidDataException>(() => GameData.ParseUnitTypes("""{ "x": { "members": 1, "speed": 1, "memberHealth": 1, "weapon": "laser" } }""", weapons));
        Assert.Throws<InvalidDataException>(() => GameData.ParseWeapons("""{ "slow": { "kind": "shell", "damage": 1, "reload": 1, "range": 5 } }"""));
        Assert.Throws<InvalidDataException>(() => GameData.ParseWeapons("""{ "blast": { "kind": "bullet", "hit": "splash", "damage": 1, "reload": 1, "range": 5 } }"""));
        Assert.Equal(HitKind.Splash, GameData.ParseWeapons("""{ "m": { "kind": "shell", "hit": "splash", "splashRadius": 2, "damage": 1, "reload": 1, "range": 5, "shellSpeed": 20 } }""")["m"].Hit);
    }

    [Fact]
    public void The_data_files_in_the_repo_load()
    {
        var data = Path.Combine(RepoRoot(), "data");
        var weapons = GameData.ParseWeapons(File.ReadAllText(Path.Combine(data, "weapons.json")));
        var types = GameData.ParseUnitTypes(File.ReadAllText(Path.Combine(data, "units.json")), weapons);
        Assert.Equal(WeaponKind.Shell, types["tank"].Gun.Kind);
        Assert.Equal(HitKind.Direct, types["tank"].Gun.Hit);
        Assert.All(types.Values, t => Assert.NotNull(t.Gun));
        var buildings = GameData.ParseBuildingTypes(File.ReadAllText(Path.Combine(data, "buildings.json")), types);
        Assert.Contains(types["tank"], buildings["factory"].Units);
        Assert.Same(types["turret"], buildings["turret"].Defense);
        Assert.Equal(WeaponType.Unarmed, types["builder"].Gun);
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Game.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("No Game.sln above the test binaries.");
    }

    [Fact]
    public void Shots_at_a_segment_land_around_its_middle_not_on_its_near_edge()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1000));
        int gun = sim.AddUnit(Blue, new Vector3(15, 0, 6), speed: 0, dps: 1, range: 10); // beside segment 1, whose middle is (15, 0, 0)
        sim.Tick([new AttackSegmentCommand(Blue, gun, 0, 1)]);

        var hits = new List<Vector3>();
        for (int t = 0; t < 40 * T; t++)
        {
            sim.Tick(NoCommands);
            var u = UnitById(sim, gun);
            if (u.LastShotTick == sim.State.Tick - 1) hits.Add(u.FireAt); // fired during the tick just run
        }
        Assert.True(hits.Count > 10);
        Assert.All(hits, h => Assert.InRange(Vector2.Distance(new(h.X, h.Z), new(15, 0)), 0, Simulation.BeltAimSpread + 1e-3f));
        Assert.True(hits.Distinct().Count() > hits.Count / 2); // not all on one point
    }
}
