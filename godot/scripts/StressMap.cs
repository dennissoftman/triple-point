using System;
using Godot;
using Sim;
using static SimConvert;

/// <summary>
/// Fills the stress scene's empty map with generated content before SimHost builds the sim from it,
/// then starts a battle: long wavy belts full of packages, a post for each side on every line, and two
/// armies that attack-move into each other. For measuring performance at MVP scale, not for play.
/// Must come before SimHost in the scene, so its _Ready runs first.
/// </summary>
public partial class StressMap : Node
{
    [Export] public SimHost Host = null!;
    [Export] public Node3D Belts = null!, Gatherers = null!, Units = null!;
    [Export] public int Lines = 10;             // belt lines, side by side
    [Export] public float LineLength = 150f;    // m each, along x
    [Export] public float LineSpacing = 10f;    // m between lines, along z
    [Export] public int UnitsPerSide = 100;
    [Export] public float WarmUp = 150f;        // s of sim run before the first frame, to fill the belts
    [Export] public float BattleAfter = 5f;     // s of sim time after that before the armies close in

    int _battleTick = -1;

    public override void _Ready()
    {
        for (int i = 0; i < Lines; i++)
        {
            float z = (i - (Lines - 1) / 2f) * LineSpacing;
            Belts.AddChild(WavyLine(z));
            Gatherers.AddChild(new OwnedMarker { Name = $"BluePost{i}", Player = 0, Position = new Vector3(-LineLength / 3, 0, z + 2.5f) });
            Gatherers.AddChild(new OwnedMarker { Name = $"RedPost{i}", Player = 1, Position = new Vector3(LineLength / 3, 0, z + 2.5f) });
        }
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < UnitsPerSide; i++) Units.AddChild(Spawn(side, i));
    }

    // First frame: runs the sim ahead with no orders, so the belts are full. A few seconds later, once:
    // every unit attack-moves to the other army's middle.
    public override void _Process(double delta)
    {
        if (_battleTick < 0)
        {
            for (int i = 0; i < WarmUp * Simulation.TicksPerSecond; i++) Host.Sim.Tick([]);
            _battleTick = Host.Sim.State.Tick + (int)(BattleAfter * Simulation.TicksPerSecond);
        }
        if (Host.Sim.State.Tick != _battleTick) return;
        foreach (var unit in Host.Sim.State.Units)
        {
            float x = unit.Owner == 0 ? 45 : -45;
            Host.Issue(new AttackMoveCommand(unit.Owner, unit.Id, ToSim(new Vector3(x, 0, unit.Position.Z))));
        }
    }

    // West to east, swinging 3 m either side every 15 m.
    Path3D WavyLine(float z)
    {
        var curve = new Curve3D();
        int points = (int)(LineLength / 15) + 1;
        for (int p = 0; p < points; p++)
        {
            float x = -LineLength / 2 + p * 15;
            var handle = new Vector3(5, 0, 0);
            curve.AddPoint(new Vector3(x, 0.2f, z + (p % 2 == 0 ? -3 : 3)), -handle, handle);
        }
        return new Path3D { Name = $"Line{z}", Curve = curve };
    }

    // A 10-wide block per side, 3 m apart, facing the enemy: squads in front, then tanks, then cars.
    UnitSpawn Spawn(int side, int i)
    {
        int row = i / 10, column = i % 10;
        float x = (side == 0 ? -1 : 1) * (40 + row * 3);
        float z = (column - 4.5f) * 3;
        string type = i < UnitsPerSide * 0.6f ? "rifle_squad" : i < UnitsPerSide * 0.85f ? "tank" : "scout_car";
        return new UnitSpawn
        {
            Name = $"{(side == 0 ? "Blue" : "Red")}{i}",
            Player = side,
            UnitType = type,
            Position = new Vector3(x, 0, z),
            RotationDegrees = new Vector3(0, side == 0 ? -90 : 90, 0), // -Z, the facing, points at the enemy
        };
    }
}
