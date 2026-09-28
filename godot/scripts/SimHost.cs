using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// Owns the simulation: turns input into commands, runs fixed 20 Hz ticks, and syncs views.
/// Views only read sim state; nothing here changes it except through commands.
/// </summary>
public partial class SimHost : Node3D
{
    const double TickSeconds = 1.0 / Simulation.TicksPerSecond;
    const int MaxTicksPerFrame = 5; // after a long stall, drop time instead of catching up forever

    [Export] public Camera3D Camera = null!;
    [Export] public Node3D Belts = null!; // each Path3D child becomes a belt line
    [Export] public BeltView BeltView = null!;
    [Export] public PackedScene UnitScene = null!;

    readonly Simulation _sim = new();
    readonly List<Command> _commands = [];
    readonly Dictionary<int, Node3D> _unitViews = [];
    double _accumulator;
    int _unit;

    public override void _Ready()
    {
        foreach (var path in Belts.GetChildren().OfType<Path3D>())
            if (path.Curve.PointCount >= 2)
                _sim.AddBeltLine(ToSegments(path), speed: 2f, spawnIntervalSeconds: 0.5f);
        BeltView.Build(_sim.State);

        _unit = _sim.AddUnit(new SVector3(-10, 0, 9), speed: 5f);
        _commands.Add(new MoveCommand(_unit, new SVector3(10, 0, -8)));
    }

    public override void _Process(double delta)
    {
        _accumulator += delta;
        int ticks = 0;
        while (_accumulator >= TickSeconds)
        {
            if (++ticks > MaxTicksPerFrame) { _accumulator = 0; break; }
            var events = _sim.Tick(_commands);
            _commands.Clear();
            foreach (var e in events)
                if (e.Kind == SimEventKind.UnitArrived) GD.Print($"Unit {e.Id} arrived at tick {_sim.State.Tick}");
            _accumulator -= TickSeconds;
        }

        float alpha = (float)(_accumulator / TickSeconds);
        SyncUnits(alpha);
        BeltView.Sync(_sim.State, alpha);
    }

    // Right-click on the ground moves the unit. No selection yet (milestone 1).
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } click) return;
        var hit = new Plane(Vector3.Up, 0).IntersectsRay(
            Camera.ProjectRayOrigin(click.Position), Camera.ProjectRayNormal(click.Position));
        if (hit is Vector3 point) _commands.Add(new MoveCommand(_unit, ToSim(point)));
    }

    void SyncUnits(float alpha)
    {
        foreach (var unit in _sim.State.Units)
        {
            if (!_unitViews.TryGetValue(unit.Id, out var view))
            {
                view = UnitScene.Instantiate<Node3D>();
                AddChild(view);
                _unitViews[unit.Id] = view;
            }
            view.GlobalPosition = ToGodot(unit.PrevPosition).Lerp(ToGodot(unit.Position), alpha);
        }
    }

    // Godot's Curve3D stores each point with in/out handles relative to it; consecutive points
    // form one cubic Bezier segment. Converted to world space so the sim never sees the node tree.
    static BezierSegment[] ToSegments(Path3D path)
    {
        var curve = path.Curve;
        var segments = new BezierSegment[curve.PointCount - 1];
        for (int i = 0; i < segments.Length; i++)
        {
            Vector3 a = curve.GetPointPosition(i), b = curve.GetPointPosition(i + 1);
            segments[i] = new BezierSegment(
                ToSim(path.ToGlobal(a)),
                ToSim(path.ToGlobal(a + curve.GetPointOut(i))),
                ToSim(path.ToGlobal(b + curve.GetPointIn(i + 1))),
                ToSim(path.ToGlobal(b)));
        }
        return segments;
    }
}
