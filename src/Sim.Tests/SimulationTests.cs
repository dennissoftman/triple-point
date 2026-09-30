using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

public class SimulationTests
{
    [Fact]
    public void Unit_moves_to_target_and_arrives()
    {
        var sim = NewSim();
        int id = sim.AddUnit(Blue, Vector3.Zero, speed: 5);
        var target = new Vector3(10, 0, 0);

        int arrivedAt = -1;
        var events = sim.Tick([new MoveCommand(Blue, id, target)]);
        for (int tick = 1; tick <= 100 && arrivedAt < 0; tick++)
        {
            if (events.Contains(new SimEvent(SimEventKind.UnitArrived, id))) arrivedAt = tick;
            else events = sim.Tick(NoCommands);
        }

        // 10 m at 5 m/s is 2 s = 40 ticks; allow one tick of float slack.
        Assert.InRange(arrivedAt, 40, 41);
        Assert.Equal(target, sim.State.Units[0].Position);
        Assert.Equal(UnitOrder.None, sim.State.Units[0].Current.Kind);
    }

    [Fact]
    public void Queued_orders_run_in_turn_and_a_plain_order_replaces_them()
    {
        var sim = NewSim();
        int id = sim.AddUnit(Blue, Vector3.Zero, speed: 5);

        int arrivals = 0;
        var events = sim.Tick([
            new MoveCommand(Blue, id, new(5, 0, 0)),
            new MoveCommand(Blue, id, new(5, 0, 5), Queued: true),
            new MoveCommand(Blue, id, new(0, 0, 5), Queued: true),
        ]);
        for (int i = 0; i < 200; i++)
        {
            arrivals += events.Count(e => e.Kind == SimEventKind.UnitArrived);
            events = sim.Tick(NoCommands);
        }
        Assert.Equal(3, arrivals);
        Assert.Equal(new Vector3(0, 0, 5), sim.State.Units[0].Position);

        sim.Tick([new MoveCommand(Blue, id, new(10, 0, 0)), new MoveCommand(Blue, id, new(10, 0, 10), Queued: true)]);
        sim.Tick([new MoveCommand(Blue, id, Vector3.Zero)]);
        Assert.Empty(sim.State.Units[0].Pending);
        Assert.Equal(Vector3.Zero, sim.State.Units[0].Current.Target);
    }

    [Fact]
    public void Queued_repair_walks_from_where_the_previous_order_ends()
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(10, 0, 0))], Belt(1));
        int id = sim.AddUnit(Blue, new Vector3(0, 0, 5), speed: 5, builds: [], repairSeconds: 5, repairCost: 4);

        sim.Tick([
            new BreakSegmentCommand(0, 0),
            new MoveCommand(Blue, id, new(8, 0, 5)),
            new RepairSegmentCommand(Blue, id, 0, 0, Queued: true),
        ]);
        for (int i = 0; i < 100 && sim.State.Units[0].Current.Kind != UnitOrder.Repair; i++) sim.Tick(NoCommands);

        var order = sim.State.Units[0].Current;
        Assert.Equal(UnitOrder.Repair, order.Kind);
        Assert.True(Vector3.Distance(new Vector3(8, 0, 0), order.Target) < 0.2f); // nearest belt point to (8, 0, 5)
    }

    [Fact]
    public void Bezier_positions_follow_arc_length_not_curve_parameter()
    {
        // Handles collapsed onto the endpoints: t is very non-uniform along the line.
        var segment = new BezierSegment(Vector3.Zero, Vector3.Zero, new(3, 0, 0), new(3, 0, 0));

        Assert.Equal(3f, segment.Length, 0.001f);
        Assert.Equal(1.5f, segment.PositionAt(1.5f).X, 0.01f);
        Assert.Equal(0.5f, segment.PositionAt(0.5f).X, 0.01f);
        Assert.Equal(Vector3.UnitX, segment.DirectionAt(0)); // zero derivative at the end is handled
    }

    [Fact]
    public void Split_cuts_a_curve_into_equal_pieces_along_the_same_path()
    {
        var arch = new BezierSegment(Vector3.Zero, new(0, 0, 6), new(10, 0, 6), new(10, 0, 0));
        var pieces = arch.Split(maxLength: 2);

        Assert.Equal((int)MathF.Ceiling(arch.Length / 2), pieces.Length);
        float along = 0;
        foreach (var piece in pieces)
        {
            Assert.Equal(arch.Length / pieces.Length, piece.Length, 0.05f);
            Assert.True(Vector3.Distance(arch.PositionAt(along), piece.PositionAt(0)) < 0.05f);
            Assert.True(Vector3.Distance(arch.PositionAt(along + piece.Length / 2), piece.PositionAt(piece.Length / 2)) < 0.05f);
            along += piece.Length;
        }
    }

    [Fact]
    public void Trucks_cross_segments_and_what_they_carry_is_lost_at_the_end()
    {
        var sim = NewSim();
        // Two 1.5 m segments, 2 m/s, one package per second.
        sim.AddBeltLine(
            [Straight(Vector3.Zero, new(1.5f, 0, 0)), Straight(new(1.5f, 0, 0), new(3, 0, 0))],
            Belt(1));
        var line = sim.State.Belts[0];

        sim.Tick(NoCommands); // tick 1 spawns the first package at the start
        Assert.Single(line.Packages);

        Run(sim, 20); // 0.1 m per tick: 2 m along, on the second segment
        Assert.Equal(1, line.Packages[0].Segment);
        Assert.Equal(2f, line.Packages[0].Position.X, 0.01f);

        Run(sim, 19); // through tick 40: the first package reaches 3 m at tick 31
        Assert.Equal(1, line.Lost);
        Assert.Single(line.Packages); // the second one, spawned at tick 21
    }

    [Fact]
    public void Dense_spawns_are_blocked_to_keep_truck_spacing()
    {
        var sim = NewSim();
        // A spawn every 0.5 m of travel, but packages need 1 m.
        sim.AddBeltLine(TwoSegments(), Belt(0.25f));
        var line = sim.State.Belts[0];

        Run(sim, 150);

        Assert.True(line.BlockedSpawns > 0);
        for (int i = 1; i < line.Packages.Count; i++)
            Assert.True(line.Packages[i - 1].Distance - line.Packages[i].Distance >= 1 - 2e-3f);
    }

    [Fact]
    public void A_broken_road_stops_trucks_before_it_and_the_ones_behind_queue()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(0.5f));
        var line = sim.State.Belts[0];
        Run(sim, 150); // the first trucks are ~5 m into segment 1
        var onIt = line.Packages.Where(p => p.Segment == 1).Select(p => (p.Id, p.Distance)).ToList();
        Assert.NotEmpty(onIt);

        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 400);

        Assert.Equal(0, line.Lost);
        Assert.Equal(0, line.Spilled);
        foreach (var (id, at) in onIt) Assert.Equal(at, line.Packages.Single(p => p.Id == id).Distance, 0.2f); // stuck where it broke
        var waiting = line.Packages.Where(p => p.Segment == 0).ToList();
        Assert.NotEmpty(waiting);
        Assert.All(waiting, p => Assert.True(p.Distance <= 10));
        Assert.Contains(waiting, p => p.Blocked);
        Assert.True(line.BlockedSpawns > 0); // the queue backs up to the source
        for (int i = 1; i < line.Packages.Count; i++)
            Assert.True(line.Packages[i - 1].Distance - line.Packages[i].Distance >= 1 - 2e-3f);
    }

    [Fact]
    public void A_destroyed_truck_spills_its_cargo_round_it_on_spots_that_do_not_overlap()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1000) with { Load = 10 });
        Run(sim, 120); // ~12 m along
        var truck = sim.State.Belts[0].Packages.Single();
        var events = sim.Tick([new DestroyCommand(truck.Id)]);

        Assert.Contains(new SimEvent(SimEventKind.TruckDestroyed, truck.Id, 0), events);
        Assert.Empty(sim.State.Belts[0].Packages);
        var pickups = sim.State.Pickups;
        Assert.Equal(10, pickups.Count);
        Assert.Equal(10, sim.State.Belts[0].Spilled);
        Assert.All(pickups, p =>
        {
            Assert.InRange(MathF.Abs(p.Position.Z), 1.49f, 2.31f); // beside the road, off it, on the ground
            Assert.InRange(p.Position.X, truck.Position.X - 2, truck.Position.X + 2);
            Assert.Equal(0f, p.Position.Y);
        });
        for (int i = 0; i < pickups.Count; i++)
            for (int j = i + 1; j < pickups.Count; j++)
                Assert.True(Vector3.Distance(pickups[i].Position, pickups[j].Position) >= 0.79f);
    }

    [Fact]
    public void Trucks_held_at_a_break_can_be_shot_and_their_cargo_picked_up()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(5) with { Load = 6 });
        var line = sim.State.Belts[0];
        sim.Tick([new BreakSegmentCommand(0, 1)]);
        Run(sim, 10 * T);
        var held = line.Packages[0];
        Assert.True(held.Blocked);

        int gun = sim.AddUnit(Blue, new Vector3(8, 0, 4), speed: 5, dps: 100);
        sim.Tick([new AttackCommand(Blue, gun, held.Id)]);
        Run(sim, 2 * T);
        Assert.DoesNotContain(line.Packages, p => p.Id == held.Id);
        Assert.Equal(6, sim.State.Pickups.Count);
        foreach (var p in sim.State.Pickups.ToList())
        {
            sim.Tick([new MoveCommand(Blue, gun, p.Position)]);
            Run(sim, 2 * T);
        }
        Assert.Equal(6, sim.State.Players[Blue].Collected);
    }

    [Fact]
    public void Units_collect_pickups_they_stand_near()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1000) with { Load = 8 });
        Run(sim, 120); // ~12 m along
        var truck = sim.State.Belts[0].Packages.Single();
        // One on each side of the road, in reach of every spot of the spill there.
        sim.AddUnit(Blue, truck.Position + new Vector3(0, 0, 1.9f), speed: 5);
        sim.AddUnit(Blue, truck.Position + new Vector3(0, 0, -1.9f), speed: 5);

        sim.Tick([new DestroyCommand(truck.Id)]);
        Run(sim, 2 * T);

        Assert.Equal(8, sim.State.Players[Blue].Collected);
        Assert.Empty(sim.State.Pickups);
    }

    [Fact]
    public void Uncollected_pickups_wait_to_be_picked_up()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1000)); // a single truck with one package
        Run(sim, 120);
        sim.Tick([new DestroyCommand(sim.State.Belts[0].Packages[0].Id)]);
        var pickup = Assert.Single(sim.State.Pickups);

        Run(sim, 10 * 60 * T); // ten minutes
        Assert.Single(sim.State.Pickups);
        sim.AddUnit(Blue, pickup.Position, speed: 0);
        Run(sim, 2);
        Assert.Empty(sim.State.Pickups);
        Assert.Equal(1, sim.State.Players[Blue].Collected);
    }

    [Fact]
    public void Builder_walks_to_broken_segment_repairs_it_paying_as_it_goes_and_flow_resumes()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1));
        var line = sim.State.Belts[0];
        sim.State.Players[Blue].Packages = 10;
        int unit = sim.AddUnit(Blue, new Vector3(10, 0, 5), speed: 5, builds: [], repairSeconds: 5, repairCost: 4); // 5 m from the segment start

        var events = sim.Tick([new BreakSegmentCommand(0, 1), new RepairSegmentCommand(Blue, unit, 0, 1)]);
        int repairedAt = -1;
        for (int tick = 1; tick <= 200 && repairedAt < 0; tick++)
        {
            if (events.Contains(new SimEvent(SimEventKind.SegmentRepaired, 0, 1))) repairedAt = tick;
            else events = sim.Tick(NoCommands);
        }

        // 2.5 m to get in range (10 ticks), then 5 s of work (100 ticks).
        Assert.InRange(repairedAt, 110, 112);
        Assert.Equal(SegmentState.Normal, line.Segments[1].State);
        Assert.Equal(UnitOrder.None, sim.State.Units[0].Current.Kind);
        Assert.Equal(10 - 4, sim.State.Players[Blue].Packages);
        Assert.Equal(0, line.Lost);

        Run(sim, 200);
        Assert.True(line.Lost > 0);
    }

    [Fact]
    public void Attacking_walks_into_range_and_breaks_the_segment_over_time()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1));
        int unit = sim.AddUnit(Blue, new Vector3(15, 0, 20), speed: 5, weapon: Blast(10)); // 20 m from segment 1

        var events = sim.Tick([new AttackSegmentCommand(Blue, unit, 0, 1)]);
        int brokenAt = -1;
        for (int tick = 1; tick <= 400 && brokenAt < 0; tick++)
        {
            if (events.Contains(new SimEvent(SimEventKind.SegmentBroken, 0, 1))) brokenAt = tick;
            else events = sim.Tick(NoCommands);
        }

        // 12 m to get within 8 m (48 ticks), then 100 health at 10 dps (200 ticks).
        Assert.InRange(brokenAt, 247, 251);
        Assert.Equal(SegmentState.Broken, sim.State.Belts[0].Segments[1].State);
        sim.Tick(NoCommands); // shots land after every unit has acted, so the order ends the tick after
        Assert.Equal(UnitOrder.None, sim.State.Units[0].Current.Kind);
        Assert.True(sim.State.Units[0].Position.Z > 7.9f); // fired from range, didn't walk up to it
    }

    [Fact]
    public void Damaged_segment_keeps_working_and_repairs_back_to_full()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1));
        var line = sim.State.Belts[0];
        var segment = line.Segments[1];
        int unit = sim.AddUnit(Blue, new Vector3(15, 0, 5), speed: 5, weapon: Blast(10)); // already in range

        sim.Tick([new AttackSegmentCommand(Blue, unit, 0, 1)]);
        Run(sim, 99); // 100 ticks of fire: half health
        sim.Tick([new MoveCommand(Blue, unit, new Vector3(15, 0, 5))]); // stop firing
        Assert.Equal(50f, segment.Health, 1f);
        Assert.Equal(SegmentState.Normal, segment.State);

        Run(sim, 300);
        Assert.True(line.Lost > 0); // packages still cross it
        Assert.Equal(0, line.Spilled);

        // Only builders repair: the gunner can't, a builder can.
        sim.State.Players[Blue].Packages = 10;
        sim.Tick([new RepairSegmentCommand(Blue, unit, 0, 1)]);
        Assert.Equal(UnitOrder.None, sim.State.Units[0].Current.Kind);
        int builder = sim.AddUnit(Blue, new Vector3(15, 0, 5), speed: 5, builds: [], repairSeconds: 5, repairCost: 4);
        sim.Tick([new RepairSegmentCommand(Blue, builder, 0, 1)]);
        Run(sim, 100); // walk ~2.5 m, then half of the 5 s full repair
        Assert.Equal(segment.MaxHealth, segment.Health);
        Assert.Equal(UnitOrder.None, UnitById(sim, builder).Current.Kind);
        Assert.Equal(10 - 4 / 2, sim.State.Players[Blue].Packages); // half the damage, half the cost
    }

    [Fact]
    public void Spill_loss_destroys_about_that_share_of_spilled_packages()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1000, spillLoss: 0.3f) with { Load = 200 });
        Run(sim, 120);
        sim.Tick([new DestroyCommand(sim.State.Belts[0].Packages[0].Id)]);
        var line = sim.State.Belts[0];
        Assert.Equal(200, line.Spilled);
        Assert.InRange(line.Destroyed / (float)line.Spilled, 0.2f, 0.4f);
    }

    [Fact]
    public void A_spilled_package_is_in_the_air_before_it_can_be_collected_and_a_smashed_one_is_gone_when_it_lands()
    {
        var sim = NewSim();
        sim.AddBeltLine(TwoSegments(), Belt(1000)); // a single truck with one package
        Run(sim, 120);
        sim.Tick([new DestroyCommand(sim.State.Belts[0].Packages[0].Id)]);
        var pickup = Assert.Single(sim.State.Pickups);
        sim.AddUnit(Blue, pickup.Position, speed: 0); // waiting where it will land
        Assert.Equal(pickup.SpilledAtTick + (int)(Simulation.SpillFallSeconds * T), pickup.LandsAtTick);

        Run(sim, pickup.LandsAtTick - sim.State.Tick - 1);
        Assert.Equal(0, sim.State.Players[Blue].Collected); // still falling
        var events = sim.Tick(NoCommands).Concat(sim.Tick(NoCommands)).ToList();
        Assert.Equal(1, sim.State.Players[Blue].Collected);
        Assert.Contains(events, e => e.Kind == SimEventKind.PickupCollected && e.Id == pickup.Id);

        var smashing = NewSim();
        smashing.AddBeltLine(TwoSegments(), Belt(1000, spillLoss: 1));
        Run(smashing, 120);
        smashing.Tick([new DestroyCommand(smashing.State.Belts[0].Packages[0].Id)]);
        var smashed = Assert.Single(smashing.State.Pickups);
        Assert.True(smashed.Smashed); // falls all the same
        smashing.AddUnit(Blue, smashed.Position, speed: 0);
        Run(smashing, (int)(Simulation.SpillFallSeconds * T) + 1);
        Assert.Empty(smashing.State.Pickups);
        Assert.Equal(0, smashing.State.Players[Blue].Collected);
    }
}
