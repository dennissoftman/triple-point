using System.Numerics;
using static Sim.Tests.TestHelpers;

namespace Sim.Tests;

/// <summary>
/// Road types: dirt and paved. A map paves a route's first stretch; builders and engineers pave dirt,
/// paid as they go. Paved pieces take PavedHealth times the damage to break, and units on paved road
/// move faster (vehicles more than infantry).
/// </summary>
public class RoadTests
{
    // 0-20 m along +X in four 5 m pieces, the first two paved.
    static Simulation NewRoad(float pavedTo = 10)
    {
        var sim = NewSim();
        sim.AddBeltLine([Straight(Vector3.Zero, new(20, 0, 0))], Belt(1000) with { MaxSegmentLength = 5 }, pavedTo: pavedTo);
        return sim;
    }

    [Fact]
    public void A_map_paves_a_routes_first_stretch_and_paved_pieces_are_tougher()
    {
        var segments = NewRoad().State.Belts[0].Segments;
        Assert.Equal([true, true, false, false], segments.Select(s => s.Paved));
        Assert.Equal(100 * Simulation.PavedHealth, segments[0].MaxHealth);
        Assert.Equal(100, segments[2].MaxHealth);
        Assert.Equal(segments[0].MaxHealth, segments[0].Health);
    }

    [Fact]
    public void A_builder_paves_a_dirt_piece_paid_as_it_works_keeping_the_damage_it_had()
    {
        var sim = NewRoad();
        sim.State.Players[Blue].Packages = 10;
        int builder = sim.AddUnit(Blue, new Vector3(12.5f, 0, 2), speed: 5, repairSeconds: 5, repairCost: 4);
        int rifles = sim.AddUnit(Blue, new Vector3(12.5f, 0, -2), speed: 5, dps: 1);
        var piece = sim.State.Belts[0].Segments[2];
        piece.Health = 60; // damaged, not broken

        int pavedAt = TicksUntil(sim, new SimEvent(SimEventKind.SegmentPaved, 0, 2), 10 * T,
            [new PaveSegmentCommand(Blue, builder, 0, 2), new PaveSegmentCommand(Blue, rifles, 0, 2)]);
        Assert.InRange(pavedAt, (int)(Simulation.PaveSeconds * T) - 2, (int)(Simulation.PaveSeconds * T) + 2);
        Assert.True(piece.Paved);
        Assert.Equal(300, piece.MaxHealth);
        Assert.Equal(260, piece.Health, 0.01f); // the 40 it had lost, still lost
        Assert.Equal(10 - Simulation.PaveCost, sim.State.Players[Blue].Packages);
        Assert.Equal(UnitOrder.None, UnitById(sim, rifles).Current.Kind); // only builders and engineers pave

        // Nothing to pave on a paved piece, and a broken one is repaired first.
        sim.Tick([new PaveSegmentCommand(Blue, builder, 0, 0), new BreakSegmentCommand(0, 3)]);
        Assert.NotEqual(UnitOrder.Pave, UnitById(sim, builder).Current.Kind);
        sim.Tick([new PaveSegmentCommand(Blue, builder, 0, 3)]);
        Assert.NotEqual(UnitOrder.Pave, UnitById(sim, builder).Current.Kind); // (it goes to repair it on its own)
    }

    [Fact]
    public void Paving_stalls_while_its_owner_is_broke()
    {
        var sim = NewRoad();
        sim.State.Players[Blue].Packages = 1;
        int builder = sim.AddUnit(Blue, new Vector3(12.5f, 0, 2), speed: 5, repairSeconds: 5, repairCost: 4);
        sim.Tick([new PaveSegmentCommand(Blue, builder, 0, 2)]);
        Run(sim, 10 * T);
        var piece = sim.State.Belts[0].Segments[2];
        Assert.False(piece.Paved);
        Assert.InRange(piece.PaveProgress, 0.2f, 0.45f); // a package buys a third of it
        sim.State.Players[Blue].Packages = 5;
        Run(sim, 10 * T);
        Assert.True(piece.Paved);
    }

    [Fact]
    public void Units_move_faster_on_paved_road_vehicles_more_than_infantry_and_not_once_it_breaks()
    {
        // How far each gets in 2 s along the road: on the paved half, on the dirt half.
        float Covered(Movement movement, float startX, bool breakIt = false)
        {
            var sim = NewRoad(pavedTo: 10);
            if (breakIt) sim.Tick([new BreakSegmentCommand(0, 0)]);
            int id = movement == Movement.Foot
                ? sim.AddUnit(Blue, new Vector3(startX, 0, 0), speed: 2)
                : sim.AddUnit(Blue, new Vector3(startX, 0, 0), speed: 2, movement: movement, acceleration: 100, turnRate: 720, heading: MathF.PI / 2);
            sim.Tick([new MoveCommand(Blue, id, new Vector3(startX + 40, 0, 0))]);
            Run(sim, 2 * T - 1);
            return UnitById(sim, id).Position.X - startX;
        }
        Assert.Equal(1 + Simulation.PavedFootBoost, Covered(Movement.Foot, 0.5f) / Covered(Movement.Foot, 10.5f), 0.05f);
        Assert.Equal(1 + Simulation.PavedVehicleBoost, Covered(Movement.Tracked, 0.5f) / Covered(Movement.Tracked, 10.5f), 0.1f);
        Assert.Equal(1, Covered(Movement.Foot, 0.5f, breakIt: true) / Covered(Movement.Foot, 10.5f), 0.05f);
    }
}
