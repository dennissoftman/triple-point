using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    [Export] public Node3D Buildings = null!; // each BuildingSpawn child is a player's starting building
    [Export] public BeltView BeltView = null!;
    [Export] public UnitsView UnitsView = null!;
    [Export] public BuildingsView BuildingsView = null!;
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public Label Hud = null!;

    [Export] public int PlayerCount = 2;
    [Export] public int StartingResources = 10;
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
    readonly List<Vector2> _homes = [];        // per player: the middle of its unit spawns, on the ground (x, z)
    double _accumulator;
    bool _demo, _perfLog;
    // Performance, over the last second of real time: how long ticks took, and how many ran.
    readonly Stopwatch _tickClock = new();
    double _perfWindow, _tickSeconds;
    int _ticksInWindow;
    string _perf = "";

    public Simulation Sim => _sim;

    /// <summary>Queues a command for the next tick.</summary>
    public void Issue(Command command) => _commands.Add(command);

    /// <summary>Where a player starts: the middle of its unit spawns, on the ground (x, z). The map center if it has none.</summary>
    public Vector2 HomeOf(int player) => player >= 0 && player < _homes.Count ? _homes[player] : Vector2.Zero;

    public override void _Ready()
    {
        // `-- --cursor-sheet=<png>`: save the placeholder cursors side by side, and quit.
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--cursor-sheet=")) is string sheet)
        {
            Cursors.SaveSheet(sheet["--cursor-sheet=".Length..]);
            SetProcess(false); // nothing is built; don't tick until the quit lands
            GetTree().Quit();
            return;
        }

        for (int p = 0; p < PlayerCount; p++)
        {
            _sim.State.Players[_sim.AddPlayer()].Resources = StartingResources;
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

        var (types, buildingTypes) = LoadData();
        foreach (var spawn in Buildings.GetChildren().OfType<BuildingSpawn>())
        {
            if (!buildingTypes.TryGetValue(spawn.BuildingType, out var type))
            {
                GD.PushWarning($"Building spawn '{spawn.Name}' has unknown type '{spawn.BuildingType}'; skipped.");
                continue;
            }
            var facing = -spawn.GlobalBasis.Z; // the exit faces the marker's -Z
            _sim.AddBuilding(spawn.Player, ToSim(spawn.GlobalPosition), type, MathF.Atan2(facing.X, facing.Z));
        }
        var spawnSums = new Vector2[PlayerCount];
        foreach (var spawn in Units.GetChildren().OfType<UnitSpawn>())
        {
            if (!types.TryGetValue(spawn.UnitType, out var type))
            {
                GD.PushWarning($"Unit spawn '{spawn.Name}' has unknown type '{spawn.UnitType}'; skipped.");
                continue;
            }
            // Units start facing the way the marker does (its -Z).
            var facing = -spawn.GlobalBasis.Z;
            float heading = MathF.Atan2(facing.X, facing.Z);
            _unitsOf[spawn.Player].Add(_sim.AddUnit(spawn.Player, ToSim(spawn.GlobalPosition), type, heading));
            spawnSums[spawn.Player] += new Vector2(spawn.GlobalPosition.X, spawn.GlobalPosition.Z);
        }
        for (int p = 0; p < PlayerCount; p++)
            _homes.Add(_unitsOf[p].Count > 0 ? spawnSums[p] / _unitsOf[p].Count : Vector2.Zero);
        BeltView.Build(_sim.State);

        // Render timings are only measured when asked for.
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        _perfLog = OS.GetCmdlineUserArgs().Contains("--perf-log");
        _demo = OS.GetCmdlineUserArgs().Contains("--demo");
        if (_demo) (GameSpeed, PlayerInput.Camera.EdgeScroll) = (3f, false); // unattended: wherever the mouse is doesn't matter
    }

    // Unit types from units.json with their weapons from weapons.json, and building types from buildings.json.
    (Dictionary<string, UnitType> Units, Dictionary<string, BuildingType> Buildings) LoadData()
    {
        var folder = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), DataDirectory));
        try
        {
            var weapons = GameData.ParseWeapons(File.ReadAllText(Path.Combine(folder, "weapons.json")));
            var units = GameData.ParseUnitTypes(File.ReadAllText(Path.Combine(folder, "units.json")), weapons);
            return (units, GameData.ParseBuildingTypes(File.ReadAllText(Path.Combine(folder, "buildings.json")), units));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            GD.PushError($"Can't load game data from {folder}: {e.Message}");
            return ([], []);
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
            _tickClock.Restart();
            var events = _sim.Tick(_commands);
            _tickSeconds += _tickClock.Elapsed.TotalSeconds;
            _ticksInWindow++;
            _commands.Clear();
            foreach (var e in events)
            {
                Log(e);
                UnitsView.OnEvent(e);
            }
            _accumulator -= TickSeconds;
        }

        float alpha = (float)(_accumulator / TickSeconds);
        UnitsView.Sync(_sim, alpha, (float)(delta * GameSpeed), PlayerInput.Selection);
        BeltView.Sync(_sim.State, alpha);
        BuildingsView.Sync(_sim.State, PlayerInput.SelectedBuilding);
        UpdatePerf(delta);
        UpdateHud();
    }

    // Once a second: frame rate, sim cost per tick, render cost (CPU and GPU), and what was drawn.
    // `--perf-log` also prints it, for unattended runs such as the stress scene.
    void UpdatePerf(double delta)
    {
        _perfWindow += delta;
        if (_perfWindow < 1) return;
        var viewport = GetViewport().GetViewportRid();
        int packages = _sim.State.Belts.Sum(l => l.Packages.Count);
        _perf = $"Perf: {Engine.GetFramesPerSecond():0} fps   sim {_tickSeconds / Math.Max(1, _ticksInWindow) * 1000:0.00} ms/tick   "
              + $"render cpu {RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport):0.00} ms, gpu {RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport):0.00} ms   "
              + $"draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}   "
              + $"objects {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame):0}   "
              + $"primitives {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:0}k   "
              + $"({_sim.State.Units.Count} units, {packages} packages, {_sim.State.Belts.Sum(l => l.Segments.Length)} segments)";
        if (_perfLog) GD.Print($"[{_sim.State.Tick}] {_perf}");
        (_perfWindow, _tickSeconds, _ticksInWindow) = (0, 0, 0);
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
                 + $"Selected {PlayerInput.Selection.Count}   LMB: select, drag: box, double-click: all of that type on screen, Shift: add   LMB your switch: flip   LMB your HQ: production\n"
                 + "RMB: move / attack enemy / hold switch (squads capture, vehicles deny) / repair damaged belt   Ctrl+RMB belt: attack   Shift: queue\n"
                 + "A then LMB: attack-move (Shift: more waypoints; RMB/Esc: cancel)      Camera: arrows / screen edge / MMB drag, wheel: zoom\n"
                 + _perf;
    }

    string DescribeSwitch(Junction j)
    {
        string feeding = j.Selected < 0
            ? $"splitting {string.Join("/", j.Outputs.Select(o => _lineNames[o]))}"
            : $"feeding {_lineNames[j.Outputs[j.Selected]]}";
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
            case SimEventKind.UnitProduced: GD.Print($"[{t}] unit {e.Id} produced at building {e.Index}"); break;
            case SimEventKind.BuildingDestroyed: GD.Print($"[{t}] building {e.Id} destroyed"); break;
            case SimEventKind.SegmentBroken: GD.Print($"[{t}] belt {e.Id} segment {e.Index} broken"); break;
            case SimEventKind.SegmentRepaired: GD.Print($"[{t}] belt {e.Id} segment {e.Index} repaired"); break;
            case SimEventKind.JunctionCaptured: GD.Print($"[{t}] junction {e.Id} captured by {PlayerPalette.Name(e.Index)}"); break;
            case SimEventKind.JunctionSwitched: GD.Print($"[{t}] junction {e.Id} now feeds {_lineNames[_sim.State.Junctions[e.Id].Outputs[e.Index]]}"); break;
        }
    }

    // `godot res://scenes/prototype.tscn -- --demo`: the prototype map (only), as a scripted match. The neutral switch splits the stream;
    // Blue takes it, which turns it to Blue's post; Blue pulls back (its vehicles back up, then turn
    // round) and Red takes it, which turns it south; Red goes for Blue's post, turrets swinging onto it
    // on the way; then Blue attack-moves into Red's side and they fight it out. Meanwhile each HQ trains
    // a squad and a car on repeat, as income allows; they gather at the rally point.
    void Demo(int tick)
    {
        const int T = Simulation.TicksPerSecond;
        const int Blue = 0, Red = 1;
        if (tick == 1)
            foreach (var hq in _sim.State.Buildings)
            {
                _commands.Add(new ProduceCommand(hq.Owner, hq.Id, "rifle_squad"));
                _commands.Add(new ProduceCommand(hq.Owner, hq.Id, "scout_car"));
                _commands.Add(new SetRepeatCommand(hq.Owner, hq.Id, true));
                if (hq.Owner == PlayerInput.LocalPlayer) PlayerInput.SelectedBuilding = hq.Id; // shows the production bar
            }
        if (!_sim.FindJunction(new SVector3(9, 0, 0), 2, out int sw))
        {
            if (tick == 0) GD.PushWarning("--demo is scripted for scenes/prototype.tscn; this map has no switch at (9, 0).");
            return;
        }
        var switchAt = _sim.State.Junctions[sw].Position with { Y = 0 };

        if (tick == 2 * T) MoveAll(Blue, switchAt);
        if (tick == 25 * T)
        {
            MoveAll(Blue, switchAt + new SVector3(0, 0, -8));                // close behind: vehicles back up...
            MoveAll(Blue, switchAt + new SVector3(0, 0, -21), queued: true); // ...then far: they turn round
        }
        if (tick == 40 * T) MoveAll(Red, switchAt);
        if (tick == 56 * T && _sim.State.Gatherers.FirstOrDefault(g => g.Owner == Blue) is { Id: > 0 } post)
            foreach (int id in _unitsOf[Red]) _commands.Add(new AttackCommand(Red, id, post.Id));
        if (tick == 80 * T) MoveAll(Blue, ToSim(new Vector3(HomeOf(Red).X, 0, HomeOf(Red).Y)), attack: true);
    }

    void MoveAll(int player, SVector3 at, bool attack = false, bool queued = false)
    {
        for (int i = 0; i < _unitsOf[player].Count; i++)
        {
            int id = _unitsOf[player][i];
            var spot = at + new SVector3((i - 1) * 3f, 0, 0);
            _commands.Add(attack ? new AttackMoveCommand(player, id, spot, queued) : new MoveCommand(player, id, spot, queued));
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
