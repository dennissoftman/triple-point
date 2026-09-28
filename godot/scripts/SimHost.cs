using System;
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
    const int MaxTicksPerFrameAt1x = 5; // after a long stall, drop time instead of catching up forever
    const float PickTolerance = 0.5f;   // m beyond the belt edge that still counts as clicking it
    static readonly float[] SpeedSteps = [1f, 1.5f, 2f, 3f];

    [Export] public Camera3D Camera = null!;
    [Export] public Node3D Belts = null!; // each Path3D child becomes a belt line
    [Export] public BeltView BeltView = null!;
    [Export] public UnitsView UnitsView = null!;
    [Export] public Label Hud = null!;

    // Game speed scales sim time per real second. The sim itself always ticks at 20 Hz of sim time.
    [Export(PropertyHint.Range, "0.25,3,0.05")] public float GameSpeed = 1f;
    [Export] public float BeltSpeed = 1f;      // m/s
    [Export] public float PackageSpacing = 1f; // m
    [Export] public float SpawnInterval = 1.5f; // s
    [Export] public float SegmentLength = 5f;  // m; authored curves are cut into breakable segments this long at most

    readonly Simulation _sim = new();
    readonly List<Command> _commands = [];
    double _accumulator;
    int _unit;
    bool _demo;

    public override void _Ready()
    {
        foreach (var path in Belts.GetChildren().OfType<Path3D>())
            if (path.Curve.PointCount >= 2)
                _sim.AddBeltLine(ToSegments(path), BeltSpeed, PackageSpacing, SpawnInterval, SegmentLength);
        BeltView.Build(_sim.State);

        _unit = _sim.AddUnit(new SVector3(-10, 0, 9), speed: 5f);
        _commands.Add(new MoveCommand(_unit, new SVector3(10, 0, -8)));

        _demo = OS.GetCmdlineUserArgs().Contains("--demo");
        if (_demo) GameSpeed = 3f;
    }

    public override void _Process(double delta)
    {
        _accumulator += delta * GameSpeed;
        int maxTicks = (int)Math.Ceiling(MaxTicksPerFrameAt1x * GameSpeed);
        int ticks = 0;
        while (_accumulator >= TickSeconds)
        {
            if (++ticks > maxTicks) { _accumulator = 0; break; }
            if (_demo) Demo(_sim.State.Tick);
            var events = _sim.Tick(_commands);
            _commands.Clear();
            foreach (var e in events) Log(e);
            _accumulator -= TickSeconds;
        }

        float alpha = (float)(_accumulator / TickSeconds);
        UnitsView.Sync(_sim, alpha);
        BeltView.Sync(_sim.State, alpha);
        UpdateHud();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                if (key.Keycode is Key.Equal or Key.KpAdd) StepSpeed(+1);
                else if (key.Keycode is Key.Minus or Key.KpSubtract) StepSpeed(-1);
                break;

            // No selection yet (milestone 1): orders always go to the one unit. Shift queues them.
            case InputEventMouseButton { Pressed: true } click when GroundPoint(click.Position) is SVector3 point:
                bool queued = click.ShiftPressed;
                bool onBelt = _sim.FindSegment(point, BeltView.BeltWidth / 2 + PickTolerance, out int line, out int segment);
                if (click.ButtonIndex == MouseButton.Left && onBelt)
                    _commands.Add(new BreakSegmentCommand(line, segment));
                else if (click.ButtonIndex == MouseButton.Right && onBelt
                         && _sim.State.Belts[line].Segments[segment].State == SegmentState.Broken)
                    _commands.Add(new RepairSegmentCommand(_unit, line, segment, queued));
                else if (click.ButtonIndex == MouseButton.Right)
                    _commands.Add(new MoveCommand(_unit, point, queued));
                break;
        }
    }

    // Snaps to the next preset up or down; a custom value from the inspector lands on the nearest one.
    void StepSpeed(int direction)
    {
        GameSpeed = direction > 0
            ? SpeedSteps.FirstOrDefault(s => s > GameSpeed + 0.01f, SpeedSteps[^1])
            : SpeedSteps.LastOrDefault(s => s < GameSpeed - 0.01f, SpeedSteps[0]);
    }

    SVector3? GroundPoint(Vector2 screen)
    {
        var hit = new Plane(Vector3.Up, 0).IntersectsRay(Camera.ProjectRayOrigin(screen), Camera.ProjectRayNormal(screen));
        return hit is Vector3 p ? ToSim(p) : null;
    }

    void UpdateHud()
    {
        var state = _sim.State;
        int onBelt = 0, spawned = 0, lost = 0, spilled = 0, blocked = 0;
        foreach (var line in state.Belts)
        {
            onBelt += line.Packages.Count;
            (spawned, lost, spilled, blocked) = (spawned + line.Spawned, lost + line.Lost, spilled + line.Spilled, blocked + line.BlockedSpawns);
        }

        Hud.Text = $"Speed {GameSpeed:0.##}x   [-] [+]      Time {state.Tick / Simulation.TicksPerSecond} s\n"
                 + $"Packages on belt {onBelt}   spawned {spawned}   lost at end {lost}   blocked at source {blocked}\n"
                 + $"Spilled {spilled}   on ground {state.Pickups.Count}   collected {state.Collected}\n"
                 + "LMB belt: break   RMB broken belt: repair   RMB ground: move   Shift: queue";
    }

    void Log(SimEvent e)
    {
        switch (e.Kind)
        {
            case SimEventKind.UnitArrived: GD.Print($"[{_sim.State.Tick}] unit {e.Id} arrived"); break;
            case SimEventKind.SegmentBroken: GD.Print($"[{_sim.State.Tick}] belt {e.Id} segment {e.Segment} broken"); break;
            case SimEventKind.SegmentRepaired: GD.Print($"[{_sim.State.Tick}] belt {e.Id} segment {e.Segment} repaired; collected {_sim.State.Collected}"); break;
        }
    }

    // `godot -- --demo`: breaks a segment near the top middle of the view once packages reach it,
    // lets it spill for a while, then queues: sweep both sides of the break, repair, walk back.
    void Demo(int tick)
    {
        const int T = Simulation.TicksPerSecond;
        if (!_sim.FindSegment(new SVector3(2, 0, -2), 3, out int line, out int segment)) return;
        var curve = _sim.State.Belts[line].Segments[segment].Curve;
        var start = curve.PositionAt(0) with { Y = 0 };
        var side = SVector3.Normalize(SVector3.Cross(curve.DirectionAt(0), SVector3.UnitY)) * 1.75f;

        if (tick == 30 * T) _commands.Add(new BreakSegmentCommand(line, segment));
        if (tick == 40 * T)
        {
            _commands.Add(new MoveCommand(_unit, start + side));
            _commands.Add(new MoveCommand(_unit, start - side, Queued: true));
            _commands.Add(new RepairSegmentCommand(_unit, line, segment, Queued: true));
            _commands.Add(new MoveCommand(_unit, new SVector3(10, 0, -8), Queued: true));
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
