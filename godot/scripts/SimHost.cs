using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Sim;
using static SimConvert;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// Owns the simulation: builds it from the map, collects commands, runs fixed 20 Hz ticks, and syncs
/// views. Views only read sim state; nothing changes it except commands. Player input lives in PlayerInput.
/// </summary>
public partial class SimHost : Node3D
{
    const double TickSeconds = 1.0 / Simulation.TicksPerSecond;
    const int MaxTicksPerFrameAt1x = 5; // after a long stall, drop time instead of catching up forever
    static readonly float[] SpeedSteps = [1f, 1.5f, 2f, 3f];

    [Export] public Node3D Belts = null!;     // each Path3D child becomes a belt line
    [Export] public Node3D Junctions = null!; // each child marks a junction; belt ends within reach attach to it
    [Export] public Node3D Gatherers = null!; // each OwnedMarker child is a player's gatherer post beside a belt
    [Export] public Node3D Units = null!;     // each UnitSpawn child is a player's starting unit
    [Export] public BeltView BeltView = null!;
    [Export] public UnitsView UnitsView = null!;
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public Label Hud = null!;

    [Export] public int PlayerCount = 2;
    [Export] public string DataDirectory = "../data"; // relative to the Godot project folder

    // Game speed scales sim time per real second. The sim itself always ticks at 20 Hz of sim time.
    [Export(PropertyHint.Range, "0.25,3,0.05")] public float GameSpeed = 1f;
    [Export] public float BeltSpeed = 1f;      // m/s
    [Export] public float PackageSpacing = 1f; // m
    [Export] public float SpawnInterval = 2f;  // s, per source
    [Export] public float SegmentLength = 5f;  // m; authored curves are cut into breakable segments this long at most
    [Export] public float SegmentHealth = 100f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float SpillLoss = 0.3f; // share of spilled packages destroyed
    [Export] public float JunctionAttachRadius = 1f; // m from a junction marker to a belt end
    [Export] public float GathererReach = 4f;        // m from a gatherer marker to the belt it pulls from

    readonly Simulation _sim = new();
    readonly List<Command> _commands = [];
    readonly List<string> _lineNames = [];     // for the HUD: the Path3D each line came from
    readonly List<List<int>> _unitsOf = [];    // starting unit ids per player, for the demo
    double _accumulator;
    bool _demo;

    public Simulation Sim => _sim;

    /// <summary>Queues a command for the next tick.</summary>
    public void Issue(Command command) => _commands.Add(command);

    public override void _Ready()
    {
        for (int p = 0; p < PlayerCount; p++)
        {
            _sim.AddPlayer();
            _unitsOf.Add([]);
        }

        var belt = new BeltConfig(BeltSpeed, PackageSpacing, SpawnInterval, SegmentLength, SegmentHealth, SpillLoss);
        foreach (var path in Belts.GetChildren().OfType<Path3D>())
        {
            if (path.Curve.PointCount < 2) continue;
            _sim.AddBeltLine(ToSegments(path), belt);
            _lineNames.Add(path.Name);
        }
        foreach (var marker in Junctions.GetChildren().OfType<Node3D>())
            _sim.AddJunction(ToSim(marker.GlobalPosition), JunctionAttachRadius);
        foreach (var marker in Gatherers.GetChildren().OfType<OwnedMarker>())
            if (_sim.AddGatherer(marker.Player, ToSim(marker.GlobalPosition), GathererReach) < 0)
                GD.PushWarning($"Gatherer '{marker.Name}' is more than {GathererReach} m from any belt; skipped.");

        var types = LoadUnitTypes();
        foreach (var spawn in Units.GetChildren().OfType<UnitSpawn>())
        {
            if (!types.TryGetValue(spawn.UnitType, out var type))
            {
                GD.PushWarning($"Unit spawn '{spawn.Name}' has unknown type '{spawn.UnitType}'; skipped.");
                continue;
            }
            _unitsOf[spawn.Player].Add(_sim.AddUnit(spawn.Player, ToSim(spawn.GlobalPosition), type));
        }
        BeltView.Build(_sim.State);

        _demo = OS.GetCmdlineUserArgs().Contains("--demo");
        if (_demo) (GameSpeed, PlayerInput.Camera.EdgeScroll) = (3f, false); // unattended: wherever the mouse is doesn't matter
    }

    Dictionary<string, UnitType> LoadUnitTypes()
    {
        var path = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), DataDirectory, "units.json"));
        try
        {
            return GameData.ParseUnitTypes(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            GD.PushError($"Can't load unit types from {path}: {e.Message}");
            return [];
        }
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
        UnitsView.Sync(_sim, alpha, (float)(delta * GameSpeed), PlayerInput.Selection, PlayerInput.LocalPlayer);
        BeltView.Sync(_sim.State, alpha);
        UpdateHud();
    }

    // Game speed is a host setting, not a sim command, so it stays here. Input Map actions, never literal keys.
    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("speed_up")) StepSpeed(+1);
        else if (e.IsActionPressed("speed_down")) StepSpeed(-1);
    }

    // Snaps to the next preset up or down; a custom value from the inspector lands on the nearest one.
    void StepSpeed(int direction)
    {
        GameSpeed = direction > 0
            ? SpeedSteps.FirstOrDefault(s => s > GameSpeed + 0.01f, SpeedSteps[^1])
            : SpeedSteps.LastOrDefault(s => s < GameSpeed - 0.01f, SpeedSteps[0]);
    }

    void UpdateHud()
    {
        var state = _sim.State;
        int onBelt = 0, lost = 0, spilled = 0, destroyed = 0, blocked = 0;
        foreach (var line in state.Belts)
        {
            (onBelt, lost, blocked) = (onBelt + line.Packages.Count, lost + line.Lost, blocked + line.BlockedSpawns);
            (spilled, destroyed) = (spilled + line.Spilled, destroyed + line.Destroyed);
        }

        var players = string.Join("      ", state.Players.Select(p =>
            $"{PlayerPalette.Name(p.Index)} {p.Resources}  (gathered {p.Gathered}, collected {p.Collected})"));
        var switches = string.Join("   ", state.Junctions.Where(j => j.IsSwitch).Select(DescribeSwitch));

        Hud.Text = $"Speed {GameSpeed:0.##}x   [-] [+]      Time {state.Tick / Simulation.TicksPerSecond} s      "
                 + $"You: {PlayerPalette.Name(PlayerInput.LocalPlayer)}  [F2: swap]\n"
                 + $"RESOURCES   {players}\n"
                 + $"{switches}\n"
                 + $"Belt: {onBelt} on it   lost at end {lost}   blocked at source {blocked}   "
                 + $"spilled {spilled} (destroyed {destroyed})   on ground {state.Pickups.Count}\n"
                 + $"Selected {PlayerInput.Selection.Count}   LMB: select, drag: box, Shift: add   LMB your switch: flip\n"
                 + "RMB: move / attack enemy / hold switch / repair damaged belt   Ctrl+RMB belt: attack   Shift+RMB: queue\n"
                 + "Camera: WASD / arrows / screen edge / MMB drag, wheel: zoom";
    }

    string DescribeSwitch(Junction j)
    {
        string feeding = j.Selected < 0 ? "closed" : $"feeding {_lineNames[j.Outputs[j.Selected]]}";
        string capture = j.CaptureProgress > 0 ? $", {PlayerPalette.Name(j.Capturer)} capturing {j.CaptureProgress:P0}" : "";
        return $"Switch: {PlayerPalette.Name(j.Owner)}, {feeding}{capture}";
    }

    void Log(SimEvent e)
    {
        int t = _sim.State.Tick;
        switch (e.Kind)
        {
            case SimEventKind.UnitDied: GD.Print($"[{t}] unit {e.Id} died"); break;
            case SimEventKind.GathererDestroyed: GD.Print($"[{t}] gatherer {e.Id} destroyed"); break;
            case SimEventKind.SegmentBroken: GD.Print($"[{t}] belt {e.Id} segment {e.Index} broken"); break;
            case SimEventKind.SegmentRepaired: GD.Print($"[{t}] belt {e.Id} segment {e.Index} repaired"); break;
            case SimEventKind.JunctionCaptured: GD.Print($"[{t}] junction {e.Id} captured by {PlayerPalette.Name(e.Index)}"); break;
            case SimEventKind.JunctionSwitched: GD.Print($"[{t}] junction {e.Id} now feeds {_lineNames[_sim.State.Junctions[e.Id].Outputs[e.Index]]}"); break;
        }
    }

    // `godot -- --demo`, on the prototype map, as a scripted match: Blue takes the switch and routes it
    // to its post; Blue pulls back and Red takes it and routes it south; Red destroys Blue's post; then
    // Blue attacks Red's units and the two sides fight it out.
    void Demo(int tick)
    {
        const int T = Simulation.TicksPerSecond;
        const int Blue = 0, Red = 1;
        _sim.FindJunction(new SVector3(9, 0, 0), 2, out int sw);
        var switchAt = _sim.State.Junctions[sw].Position with { Y = 0 };

        if (tick == 2 * T) MoveAll(Blue, switchAt);
        if (tick == 12 * T) _commands.Add(new SetJunctionCommand(Blue, sw, 0));
        if (tick == 40 * T)
        {
            MoveAll(Blue, new SVector3(14, 0, -12));
            MoveAll(Red, switchAt);
        }
        if (tick == 52 * T) _commands.Add(new SetJunctionCommand(Red, sw, 1));
        if (tick == 56 * T && _sim.State.Gatherers.FirstOrDefault(g => g.Owner == Blue) is { Id: > 0 } post)
            foreach (int id in _unitsOf[Red]) _commands.Add(new AttackCommand(Red, id, post.Id));
        if (tick == 80 * T)
            foreach (int id in _unitsOf[Blue])
                for (int k = 0; k < _unitsOf[Red].Count; k++)
                    _commands.Add(new AttackCommand(Blue, id, _unitsOf[Red][k], Queued: k > 0));
    }

    void MoveAll(int player, SVector3 at)
    {
        for (int i = 0; i < _unitsOf[player].Count; i++)
            _commands.Add(new MoveCommand(player, _unitsOf[player][i], at + new SVector3((i - 1) * 3f, 0, 0)));
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
