using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Sim;
using static SimConvert;
using SVector3 = System.Numerics.Vector3;

/// <summary>
/// Owns the simulation: builds it from the map, collects commands, runs fixed 20 Hz ticks, and syncs
/// views. Views only read sim state; nothing changes it except commands. Player input lives in PlayerInput.
/// In a networked match (MatchSetup Networked) commands go through NetSession's Lockstep, ticks run only
/// once both players' turns are in, at 1x, and a desync stops the match and writes a report: each side
/// rebuilds the start, plays its own replay to the bad tick and dumps the state there; the joiner sends
/// its dump to the host, which compares them.
/// </summary>
public partial class SimHost : Node3D
{
    const double TickSeconds = 1.0 / Simulation.TicksPerSecond;
    const int MaxTicksPerFrameAt1x = 5; // after a long stall, drop time instead of catching up forever
    static readonly float[] SpeedSteps = [1f, 1.5f, 2f, 3f];

    [Export] public Node3D Belts = null!;     // each Path3D child becomes a belt line
    [Export] public Node3D Gatherers = null!; // each OwnedMarker child is a player's gatherer post beside a belt
    [Export] public Node3D Units = null!;     // each UnitSpawn child is a player's starting unit
    [Export] public Node3D Buildings = null!; // each BuildingSpawn child is a player's starting building
    [Export] public Node3D? Obstacles;        // each MapObstacle child is solid ground; the map is the camera's bounds
    [Export] public BeltView BeltView = null!;
    [Export] public UnitsView UnitsView = null!;
    [Export] public BuildingsView BuildingsView = null!;
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public Label Hud = null!;

    [Export] public int PlayerCount = 2;
    [Export] public int StartingPackages = 50;
    [Export] public bool EndConditions = true; // players can lose and the game end; off for maps without buildings
    [Export] public bool ShowDebug;            // the debug text under the HUD line; toggle_debug flips it
    [Export] public int BrokenSegments;        // on the whole map, as of the last frame; for tools
    [Export] public int ShownTick;             // the sim's tick as of the last frame; for tools
    [Export] public bool Paused;               // the pause menu is open: no ticks run
    [Export] public bool FogOfWar = true;      // `--reveal` shows everything anyway (spectating), as does the demo
    [Export] public int[] AiPlayers = [];      // players the commander AI plays; `--ai=1`, `--ai=0,1` or `--ai=none` overrides
    [Export] public global::Sim.Ai.AiLevel AiLevel = global::Sim.Ai.AiLevel.Normal; // how hard it plays; `--ai-level=easy|normal` overrides
    [Export] public bool MatchReports = true;  // write a report per match (user://matches) at game over, or on leaving one that ran a minute; never in the demo
    [Export] public string DataDirectory = "../data"; // relative to the Godot project folder

    // Game speed scales sim time per real second. The sim itself always ticks at 20 Hz of sim time.
    [Export(PropertyHint.Range, "0.25,3,0.05")] public float GameSpeed = 1f;
    // Supply routes (in the code, belts): their trucks and road.
    [Export] public float BeltSpeed = 5f;      // m/s, trucks
    [Export] public float PackageSpacing = 10f; // m, the least between trucks
    [Export] public float SpawnInterval = 12f; // s, a truck per source
    [Export] public int TruckLoad = 12;        // packages a truck leaves with
    [Export] public float TruckHealth = 100f;
    [Export] public bool StartFull = true;     // trucks already all along the routes at the start
    [Export] public float SegmentLength = 5f;  // m; authored curves are cut into breakable segments this long at most
    [Export] public float SegmentHealth = 100f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float SpillLoss = 0.3f; // share of spilled packages destroyed
    [Export] public int SourceSupply;          // packages each source holds at the start; 0: unlimited
    [Export] public float GathererReach = 4f;        // m from a gatherer marker to the belt it pulls from

    Simulation _sim = null!;
    readonly List<Command> _commands = [];
    // A networked match: its lockstep, and how it's going.
    Lockstep? _net;
    uint _seed = 1;
    int _faultAt = -1, _checkEvery, _quitAt = -1; // debug: --net-fault-at, --net-check, --net-quit-at
    double _waiting;                              // s the next tick has waited for the other player
    double _quitIn = -1;                          // s until a tool run quits
    bool _desyncShown, _leftShown, _compared;
    Task<string>? _dumpTask;
    string? _dump, _reportFolder;
    Label _netLabel = null!;
    readonly List<Sim.Ai.Commander> _ais = [];
    MatchStats _stats = null!;
    bool _reported;
    const int ReportAfterTicks = 60 * Simulation.TicksPerSecond; // a match left before this isn't worth a report
    FogOverlay _fog = null!;
    bool _reveal;
    const double AlertSeconds = 5;

    /// <summary>The fog drawn over the world; its texture shades the minimap too.</summary>
    public FogOverlay Fog => _fog;

    /// <summary>Where something happened the local player should know about (a belt cut upstream of its posts), and until when (real seconds).</summary>
    public readonly List<(SVector3 At, double Until)> Alerts = [];
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

    /// <summary>Queues a command for the next tick (in a networked match: for this side's next turn).</summary>
    public void Issue(Command command)
    {
        if (_net is not null) _net.Issue(command);
        else _commands.Add(command);
    }

    /// <summary>A networked match: no restart, no speed change, pausing pauses both.</summary>
    public bool Networked => _net is not null;

    /// <summary>Who paused (in a networked match, maybe the other player).</summary>
    public int PausedBy => _net?.PausedBy ?? PlayerInput.LocalPlayer;

    /// <summary>Opens or closes the pause; in a networked match, for both players.</summary>
    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (_net is not null && _net.Paused != paused) _net.SetPaused(paused);
    }

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

        var setup = MatchSetup.Current;
        _net = setup is { Networked: true } ? NetSession.Instance?.Match : null;
        _seed = setup?.Seed ?? 1;
        if (_net is null && Arg("--seed=") is string seed) _seed = uint.Parse(seed);
        _sim = BuildSim(_seed, _unitsOf, _homes);
        BeltView.Build(_sim.State);
        BuildInterface();
        Hud.MouseFilter = Control.MouseFilterEnum.Pass; // for its tooltip: the keys it no longer spells out
        Hud.TooltipText = L.T("hud.tip", CommandCard.KeyOf("speed_down"), CommandCard.KeyOf("speed_up"), CommandCard.KeyOf("debug_swap_player"), CommandCard.KeyOf("toggle_debug"));

        // Render timings are only measured when asked for.
        RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        _perfLog = OS.GetCmdlineUserArgs().Contains("--perf-log");
        _demo = OS.GetCmdlineUserArgs().Contains("--demo");
        _reveal = _demo || OS.GetCmdlineUserArgs().Contains("--reveal");
        _sim.Vision(0); // sizes the fog's grid
        AddChild(_fog = new FogOverlay { Name = "Fog" });
        _fog.Setup(_sim);
        // Who plays what: the scene's exports, then the skirmish setup (MatchSetup), then the command line.
        if (setup is not null)
        {
            PlayerInput.LocalPlayer = setup.LocalPlayer;
            (AiPlayers, AiLevel) = (setup.Hotseat || setup.Networked ? [] : [1 - setup.LocalPlayer], setup.Level);
            PlayerInput.Camera.Focus = HomeOf(setup.LocalPlayer);
        }
        if (_net is null && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--ai=")) is string ai)
            AiPlayers = ai[5..] == "none" ? [] : ai[5..].Split(',').Select(int.Parse).ToArray();
        if (_net is not null)
        {
            GameSpeed = 1;
            // Tools: the AI plays this side (its commands go through lockstep like clicks); print the hash
            // every N ticks, and quit at a tick, to compare two machines' runs; a fault, to test the desync path.
            if (OS.GetCmdlineUserArgs().Contains("--net-ai")) AiPlayers = [PlayerInput.LocalPlayer];
            _checkEvery = Arg("--net-check=") is string every ? int.Parse(every) : 0;
            _quitAt = Arg("--net-quit-at=") is string quit ? int.Parse(quit) : -1;
            _faultAt = Arg("--net-fault-at=") is string fault ? int.Parse(fault) : -1;
            GD.Print($"Networked match: seed {_seed}, you are {PlayerPalette.Name(PlayerInput.LocalPlayer)}, delay {_net.Delay} ticks, start {_sim.Hash().All:x16}");
        }
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--ai-level=")) is string level)
        {
            if (Enum.TryParse<global::Sim.Ai.AiLevel>(level["--ai-level=".Length..], ignoreCase: true, out var parsed)) AiLevel = parsed;
            else GD.PushWarning($"Unknown AI level in '{level}'; easy or normal.");
        }
        if (!_demo)
            foreach (int p in AiPlayers)
                if (p >= 0 && p < _sim.State.Players.Count) _ais.Add(new Sim.Ai.Commander(_sim, p, AiLevel));
        _stats = new MatchStats(_sim);
        if (_net is not null) _net.Replay.Setup = GetTree().CurrentScene?.SceneFilePath ?? MatchSetup.MatchScene;
        MatchReports &= EndConditions && !_demo;
        if (_demo) (GameSpeed, PlayerInput.Camera.EdgeScroll) = (3f, false); // unattended: wherever the mouse is doesn't matter
        // Now, not only each frame: views may draw before the first one, and Sight still holds the last
        // scene's sim (another map, after Main menu and Skirmish).
        (Sight.Sim, Sight.Player, Sight.All) = (_sim, PlayerInput.LocalPlayer, _reveal || !_sim.FogOfWar);
    }

    // The screen's layout: a slim bar across the top (the HUD line at its left, packages in the middle,
    // alerts at the right); the minimap bottom-left with the idle-builder button above it; what's selected
    // bottom-center; the command card bottom-right. The scene has the HUD line, packages panel, minimap and
    // command card; the rest is made here.
    void BuildInterface()
    {
        var ui = Hud.GetParent();
        var top = new TopBar { Name = "TopBar", Host = this };
        ui.AddChild(top);
        ui.MoveChild(top, 0); // behind the rest
        Hud.Position = new Vector2(16, 7);
        ui.AddChild(new SelectionPanel { Name = "SelectionPanel", PlayerInput = PlayerInput });
        ui.AddChild(new IdleBuilderButton { Name = "IdleBuilders", PlayerInput = PlayerInput, Minimap = ui.GetChildren().OfType<Minimap>().FirstOrDefault() });
        // A networked match's notice under the top strip: waiting for the other player.
        _netLabel = new Label { Name = "NetNotice", HorizontalAlignment = HorizontalAlignment.Center, Visible = false, LabelSettings = new LabelSettings { FontSize = 20, OutlineSize = 4, OutlineColor = Colors.Black } };
        _netLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        (_netLabel.Position, _netLabel.GrowHorizontal) = (new Vector2(_netLabel.Position.X, 60), Control.GrowDirection.Both);
        ui.AddChild(_netLabel);
    }

    static string? Arg(string prefix) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..];

    /// <summary>
    /// The match as the map scene sets it up, from its nodes: players, navigation over the camera's
    /// bounds, rocks, routes, depots, buildings and units. Called once for the match, and again after a
    /// desync to rebuild the same start (then without `unitsOf` and `homes`). Headings go through SimMath,
    /// like everything that feeds the sim, so both machines start from the same bits.
    /// </summary>
    Simulation BuildSim(uint seed, List<List<int>>? unitsOf, List<Vector2>? homes)
    {
        var sim = new Simulation(seed);
        for (int p = 0; p < PlayerCount; p++)
        {
            sim.State.Players[sim.AddPlayer()].Packages = StartingPackages;
            unitsOf?.Add([]);
        }

        // Navigation covers the map, which is where the camera may look.
        var map = PlayerInput.Camera.Bounds;
        sim.EnableNavigation(map.Position.X, map.Position.Y, map.End.X, map.End.Y);
        foreach (var rock in Obstacles?.GetChildren().OfType<MapObstacle>() ?? [])
        {
            var facing = -rock.GlobalBasis.Z;
            sim.AddObstacle(ToSim(rock.GlobalPosition), rock.Size.X / 2, rock.Size.Z / 2, SimMath.Atan2(facing.X, facing.Z));
        }

        var belt = new BeltConfig(BeltSpeed, PackageSpacing, SpawnInterval, SegmentLength, SegmentHealth, SpillLoss, SourceSupply, TruckLoad, TruckHealth, StartFull);
        foreach (var path in Belts.GetChildren().OfType<Path3D>())
        {
            if (path.Curve.PointCount < 2) continue;
            var (coveredStart, coveredEnd, pavedTo) = path is BeltPath b ? (b.CoveredStart, b.CoveredEnd, b.PavedTo) : (0f, 0f, 0f);
            sim.AddBeltLine(ToSegments(path), belt, coveredStart, coveredEnd, pavedTo);
        }
        foreach (var marker in Gatherers.GetChildren().OfType<OwnedMarker>())
            if (sim.AddGatherer(marker.Player, ToSim(marker.GlobalPosition), GathererReach) < 0)
                GD.PushWarning($"Gatherer '{marker.Name}' is more than {GathererReach} m from any belt; skipped.");

        var (types, buildingTypes) = LoadData();
        sim.BuildingTypes = buildingTypes;
        sim.EndConditions = EndConditions;
        sim.FogOfWar = FogOfWar;
        foreach (var spawn in Buildings.GetChildren().OfType<BuildingSpawn>())
        {
            if (!buildingTypes.TryGetValue(spawn.BuildingType, out var type))
            {
                GD.PushWarning($"Building spawn '{spawn.Name}' has unknown type '{spawn.BuildingType}'; skipped.");
                continue;
            }
            var facing = -spawn.GlobalBasis.Z; // the exit faces the marker's -Z
            sim.AddBuilding(spawn.Player, ToSim(spawn.GlobalPosition), type, SimMath.Atan2(facing.X, facing.Z));
        }
        var spawnSums = new Vector2[PlayerCount];
        var spawnCounts = new int[PlayerCount];
        foreach (var spawn in Units.GetChildren().OfType<UnitSpawn>())
        {
            if (!types.TryGetValue(spawn.UnitType, out var type))
            {
                GD.PushWarning($"Unit spawn '{spawn.Name}' has unknown type '{spawn.UnitType}'; skipped.");
                continue;
            }
            // Units start facing the way the marker does (its -Z).
            var facing = -spawn.GlobalBasis.Z;
            int id = sim.AddUnit(spawn.Player, ToSim(spawn.GlobalPosition), type, SimMath.Atan2(facing.X, facing.Z));
            unitsOf?[spawn.Player].Add(id);
            spawnSums[spawn.Player] += new Vector2(spawn.GlobalPosition.X, spawn.GlobalPosition.Z);
            spawnCounts[spawn.Player]++;
        }
        for (int p = 0; p < PlayerCount; p++)
            homes?.Add(spawnCounts[p] > 0 ? spawnSums[p] / spawnCounts[p] : Vector2.Zero);
        return sim;
    }

    // Unit types from units.json with their weapons from weapons.json, and building types from buildings.json.
    (Dictionary<string, UnitType> Units, Dictionary<string, BuildingType> Buildings) LoadData()
    {
        try
        {
            var texts = GameFiles.Read(DataDirectory);
            var weapons = GameData.ParseWeapons(texts[0]);
            var units = GameData.ParseUnitTypes(texts[1], weapons);
            return (units, GameData.ParseBuildingTypes(texts[2], units));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            GD.PushError($"Can't load game data from {GameFiles.Folder(DataDirectory)}: {e.Message}");
            return ([], []);
        }
    }

    public override void _Process(double delta)
    {
        if (_net is not null) NetTicks(delta);
        else
        {
            if (!Paused) _accumulator += delta * GameSpeed;
            int maxTicks = (int)Math.Ceiling(MaxTicksPerFrameAt1x * GameSpeed);
            int ticks = 0;
            while (_accumulator >= TickSeconds)
            {
                if (++ticks > maxTicks) { _accumulator = 0; break; }
                if (_demo) Demo(_sim.State.Tick);
                foreach (var ai in _ais) _commands.AddRange(ai.Think());
                RunTick(_commands);
                _commands.Clear();
                _accumulator -= TickSeconds;
            }
        }

        float alpha = (float)(_accumulator / TickSeconds);
        (Sight.Sim, Sight.Player, Sight.All) = (_sim, PlayerInput.LocalPlayer, _reveal || !_sim.FogOfWar);
        _fog.Sync((float)delta);
        UnitsView.Sync(_sim, alpha, (float)(delta * GameSpeed), PlayerInput.Selection, PlayerInput.Placing);
        BeltView.Sync(_sim.State, alpha);
        BuildingsView.Sync(_sim.State, PlayerInput.SelectedBuilding, PlayerInput.Placing);
        UpdatePerf(delta);
        UpdateHud();
    }

    void RunTick(IReadOnlyList<Command> commands)
    {
        _tickClock.Restart();
        var events = _sim.Tick(commands);
        _tickSeconds += _tickClock.Elapsed.TotalSeconds;
        _stats.Observe(_sim, events);
        _ticksInWindow++;
        foreach (var e in events)
        {
            Log(e);
            if (e.Kind == SimEventKind.GameOver) Report();
            Alert(e);
            UnitsView.OnEvent(e);
            BeltView.OnEvent(e, _sim.State);
        }
    }

    // A networked match's frame: ticks run as real time allows and both turns are in (catching up a few
    // a frame after a wait), this side's turns leave at once, and the match's state shows: waiting for the
    // other player, paused by either, out of sync, or left.
    void NetTicks(double delta)
    {
        var net = _net!;
        if (!net.Paused) _accumulator = Math.Min(_accumulator + delta, MaxBehindSeconds);
        int ticks = 0;
        bool stalled = false;
        while (_accumulator >= TickSeconds && ticks < MaxTicksPerFrameAt1x)
        {
            if (!net.Ready(_sim.State.Tick)) { stalled = true; break; }
            foreach (var ai in _ais) foreach (var c in ai.Think()) net.Issue(c);
            if (_sim.State.Tick == _faultAt && _sim.State.Units.FirstOrDefault(u => u.Owner == 0) is { Id: > 0 } victim)
            {
                GD.Print($"[{_sim.State.Tick}] --net-fault-at: destroying unit {victim.Id} here only");
                net.InjectFault(new DestroyCommand(victim.Id));
            }
            RunTick(net.Begin(_sim));
            _accumulator -= TickSeconds;
            ticks++;
            int tick = _sim.State.Tick;
            if (_checkEvery > 0 && tick % _checkEvery == 0) GD.Print($"net-check {tick} {_sim.Hash().All:x16}");
            if (tick == _quitAt) { GD.Print($"net-quit {tick} {_sim.Hash().All:x16}"); GetTree().Quit(); break; }
        }
        NetSession.Instance.Flush();
        _waiting = stalled && !net.Paused && net.Desync is null && net.Violation is null && !net.PeerLeft ? _waiting + delta : 0;
        if (_quitIn >= 0 && (_quitIn -= delta) < 0)
        {
            if (Arg("--net-snap=") is string snap) GetViewport().GetTexture().GetImage().SavePng(snap); // tools: the screen as it ends
            GetTree().Quit();
        }

        if (net.Paused != Paused) PlayerInput.PauseMenu.SetOpen(net.Paused);
        string other = PlayerPalette.Name(1 - PlayerInput.LocalPlayer);
        _netLabel.Text = _waiting > WaitShownAfter ? L.T("net.waiting", other) : "";
        _netLabel.Visible = _netLabel.Text.Length > 0;
        if (net.Violation is string violation)
        {
            if (!_leftShown)
            {
                _leftShown = true;
                GD.Print($"[{_sim.State.Tick}] VIOLATION: {other}'s game sent {violation}; the match stopped");
                GameOver.ShowEnd(L.T("net.violation", other), L.T("net.violation.detail"), Colors.Orange); // what exactly: the log
            }
        }
        else if (net.Desync is var (desyncTick, sections)) ShowDesync(desyncTick, sections);
        else if (net.PeerLeft && !_leftShown && !_sim.State.GameOver)
        {
            _leftShown = true;
            GD.Print($"[{_sim.State.Tick}] {other} left the match");
            GameOver.ShowEnd(L.T("net.left", other), "", PlayerPalette.Color(1 - PlayerInput.LocalPlayer));
        }
    }

    const double MaxBehindSeconds = 0.5, WaitShownAfter = 0.3;

    /// <summary>The banner, for a networked match's endings (set in the scene; found if not).</summary>
    GameOverOverlay GameOver => _gameOver ??= Hud.GetParent().GetChildren().OfType<GameOverOverlay>().First();
    GameOverOverlay? _gameOver;

    // Out of sync: stop, say so, and write the report. Each side plays its own replay on a freshly built
    // start to the first bad tick (in the background: a long match takes a few seconds) and dumps the state
    // there; the joiner sends its dump to the host, which lists the first differences.
    void ShowDesync(int tick, string sections)
    {
        var net = _net!;
        string when = $"{tick / Simulation.TicksPerSecond / 60}:{tick / Simulation.TicksPerSecond % 60:00}";
        if (!_desyncShown)
        {
            _desyncShown = true;
            GD.Print($"[{_sim.State.Tick}] DESYNC at tick {tick}: {sections}");
            _reportFolder = ProjectSettings.GlobalizePath($"user://desyncs/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{PlayerPalette.Name(PlayerInput.LocalPlayer).ToLowerInvariant()}");
            Directory.CreateDirectory(_reportFolder);
            using (var file = File.Create(Path.Combine(_reportFolder, "replay.tprp"))) net.Replay.Save(file);
            var fresh = BuildSim(_seed, null, null);
            var replay = net.Replay;
            _dumpTask = Task.Run(() => { replay.PlayTo(fresh, tick); return StateDump.Write(fresh); });
            GameOver.ShowEnd(L.T("net.desync", when), L.T("net.desync.preparing", sections), Colors.Orange);
        }
        if (_dump is null && _dumpTask is { IsCompleted: true })
        {
            _dump = _dumpTask.Result;
            File.WriteAllText(Path.Combine(_reportFolder!, "state-here.txt"), _dump);
            if (!NetSession.Instance.IsHost) net.SendDump(_dump);
            NetSession.Instance.Flush();
            GD.Print($"Desync report: {_reportFolder}");
            GameOver.ShowEnd(L.T("net.desync", when), L.T(NetSession.Instance.IsHost ? "net.desync.waiting" : "net.desync.sent", _reportFolder!), Colors.Orange);
            if (_quitAt >= 0 && !NetSession.Instance.IsHost) _quitIn = 5; // a tool run: its part is done, once the dump is across
        }
        if (!_compared && _dump is not null && net.PeerDump is string theirs)
        {
            _compared = true;
            File.WriteAllText(Path.Combine(_reportFolder!, "state-there.txt"), theirs);
            var differences = StateDump.Compare(_dump, theirs, 40);
            File.WriteAllLines(Path.Combine(_reportFolder!, "differences.txt"), differences);
            foreach (var d in differences.Take(8)) GD.Print("  " + d);
            var first = differences.FirstOrDefault(d => !d.StartsWith("sim.hash")) ?? "";
            GameOver.ShowEnd(L.T("net.desync", when), L.T("net.desync.compared", first, _reportFolder!), Colors.Orange);
            if (_quitAt >= 0) _quitIn = 0.5; // a tool run: done (after a frame or two of the banner, for --net-snap)
        }
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
        else if (e.IsActionPressed("toggle_debug")) ShowDebug = !ShowDebug;
    }

    // Snaps to the next preset up or down; a custom value from the inspector lands on the nearest one.
    void StepSpeed(int direction)
    {
        if (_net is not null) return; // both players' games run at 1x
        GameSpeed = direction > 0
            ? SpeedSteps.FirstOrDefault(s => s > GameSpeed + 0.01f, SpeedSteps[^1])
            : SpeedSteps.LastOrDefault(s => s < GameSpeed - 0.01f, SpeedSteps[0]);
    }

    // One line of what matters in play (speed, time, whose side you're on, anyone's rebuild clock); the
    // rest (every side's packages, belt counters, controls, performance) under toggle_debug.
    // The local player's packages have their own panel (PackagePanel).
    void UpdateHud()
    {
        var state = _sim.State;
        int broken = 0;
        foreach (var line in state.Belts)
            foreach (var segment in line.Segments)
                if (segment.State == SegmentState.Broken) broken++;
        BrokenSegments = broken;
        ShownTick = state.Tick;
        int seconds = state.Tick / Simulation.TicksPerSecond;
        var hud = L.T("hud.line", GameSpeed.ToString("0.##"), $"{seconds / 60}:{seconds % 60:00}", PlayerPalette.Name(PlayerInput.LocalPlayer));
        if (_net is not null) hud += "      " + L.T("net.ping", NetSession.Instance.Ping);
        double now = Time.GetTicksMsec() / 1000.0;
        Alerts.RemoveAll(a => a.Until < now); // the top bar shows them
        foreach (var p in state.Players)
        {
            if (p.Lost) hud += "\n" + L.T("hud.out", PlayerPalette.Name(p.Index));
            else if (p.GraceTicksLeft >= 0)
                hud += "\n" + L.T("hud.rebuild", PlayerPalette.Name(p.Index), (p.GraceTicksLeft + Simulation.TicksPerSecond - 1) / Simulation.TicksPerSecond)
                     + (p.GracePaused ? L.T("hud.rebuild.paused") : "");
        }
        Hud.Text = ShowDebug ? hud + "\n\n" + DebugText() : hud;
    }

    string DebugText()
    {
        var state = _sim.State;
        int onBelt = 0, lost = 0, returned = 0, spilled = 0, destroyed = 0, blocked = 0;
        foreach (var line in state.Belts)
        {
            (onBelt, lost, blocked) = (onBelt + line.Packages.Count, lost + line.Lost, blocked + line.BlockedSpawns);
            returned += line.Returned;
            (spilled, destroyed) = (spilled + line.Spilled, destroyed + line.Destroyed);
        }

        var players = string.Join("      ", state.Players.Select(p =>
            $"{PlayerPalette.Name(p.Index)} {p.Packages}  (gathered {p.Gathered}, collected {p.Collected}, spent {p.Spent})"));

        return $"packages   {players}\n"
                 + $"Sources: {string.Join(" / ", state.Belts.Select(l => l.Finite ? $"{l.Reserve} of {l.Supply}" : "unlimited"))}   "
                 + $"Belt: {onBelt} on it   returned at end {returned}   lost at end {lost}   blocked at source {blocked}   "
                 + $"spilled {spilled} (destroyed {destroyed})   on ground {state.Pickups.Count}\n"
                 + $"Selected {PlayerInput.Selection.Count}   LMB: select, drag: box, double-click: all of that type on screen, Shift: add   LMB your building: its card\n"
                 + "RMB: move / attack enemy / repair damaged belt   Ctrl+RMB belt: attack   Shift: queue\n"
                 + "A then LMB: attack-move (Shift: more waypoints; RMB/Esc: cancel)      Camera: arrows / screen edge / MMB drag, wheel: zoom\n"
                 + _perf;
    }

    // A break upstream of one of the local player's posts: it's told, wherever it happened.
    void Alert(SimEvent e)
    {
        if (e.Kind != SimEventKind.SegmentBroken || !_sim.FeedsPostOf(PlayerInput.LocalPlayer, e.Id, e.Index)) return;
        var curve = _sim.State.Belts[e.Id].Segments[e.Index].Curve;
        Alerts.Add((curve.PositionAt(curve.Length / 2), Time.GetTicksMsec() / 1000.0 + AlertSeconds));
    }

    // Leaving a match (restart, quit, closing the window) that ran long enough: its report, if not written yet.
    public override void _ExitTree()
    {
        if (_stats is not null && _sim.State.Tick >= ReportAfterTicks) Report();
        if (_net is not null)
        {
            // Every networked match keeps its replay (user://replays), to check or compare later.
            var folder = ProjectSettings.GlobalizePath("user://replays");
            Directory.CreateDirectory(folder);
            using (var file = File.Create(Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{PlayerPalette.Name(PlayerInput.LocalPlayer).ToLowerInvariant()}.tprp")))
                _net.Replay.Save(file);
            if (NetSession.Instance.Match == _net) NetSession.Instance.Leave();
        }
        if (Sight.Sim == _sim) (Sight.Sim, Sight.All) = (null, true);
    }

    void Report()
    {
        if (!MatchReports || _reported) return;
        _reported = true;
        _stats.Finish(_sim.State);
        string ai = (_ais.Count == 0 ? "none" : string.Join(", ", _ais.Select(a => PlayerPalette.Name(a.Player))) + $" ({AiLevel})")
                    + (_net is not null ? $"; networked, seed {_seed}" : "");
        if (MatchReport.Write(_stats, _sim.State, GetTree().CurrentScene?.SceneFilePath ?? "", ai) is string path)
            GD.Print($"Match report: {path}");
    }

    // A unit, building or post as the log names it: owner, type, id.
    string Who(int id)
    {
        int owner = _stats.OwnerOf(id);
        string type = _stats.TypeOf(id);
        return $"{(owner >= 0 ? PlayerPalette.Name(owner) + " " : "")}{(type.Length > 0 ? type : "unit")} {id}";
    }

    void Log(SimEvent e)
    {
        int t = _sim.State.Tick;
        switch (e.Kind)
        {
            case SimEventKind.UnitDied: GD.Print($"[{t}] {Who(e.Id)} died"); break;
            case SimEventKind.GathererDestroyed: GD.Print($"[{t}] {Who(e.Id)} destroyed"); break;
            case SimEventKind.TruckDestroyed: GD.Print($"[{t}] truck {e.Id} destroyed on road {e.Index}"); break;
            case SimEventKind.UnitProduced: GD.Print($"[{t}] {Who(e.Id)} trained at {Who(e.Index)}"); break;
            case SimEventKind.BuildingDestroyed: GD.Print($"[{t}] {Who(e.Id)} destroyed"); break;
            case SimEventKind.BuildingPlaced: GD.Print($"[{t}] {Who(e.Id)} placed by {Who(e.Index)}"); break;
            case SimEventKind.BuildingCompleted: GD.Print($"[{t}] {Who(e.Id)} completed" + (e.Index != e.Id ? $", now {Who(e.Index)}" : "")); break;
            case SimEventKind.BuildBlocked: GD.Print($"[{t}] {Who(e.Id)} couldn't build: " + (e.Index switch { Simulation.BlockedByMoney => "not enough packages", Simulation.BlockedByRequirement => "missing what it requires", _ => "the site is taken" })); break;
            case SimEventKind.GraceStarted: GD.Print($"[{t}] {PlayerPalette.Name(e.Id)} has no buildings: {Simulation.GraceSeconds:0} s to rebuild"); break;
            case SimEventKind.GraceEnded: GD.Print($"[{t}] {PlayerPalette.Name(e.Id)} rebuilt"); break;
            case SimEventKind.PlayerLost: GD.Print($"[{t}] {PlayerPalette.Name(e.Id)} lost"); break;
            case SimEventKind.GameOver: GD.Print($"[{t}] game over: " + (e.Id == Player.None ? "a draw" : $"{PlayerPalette.Name(e.Id)} wins")); break;
            case SimEventKind.SegmentBroken:
                int by = _sim.State.Belts[e.Id].Segments[e.Index].BrokenBy;
                GD.Print($"[{t}] belt {e.Id} segment {e.Index} broken" + (by >= 0 ? $" by {PlayerPalette.Name(by)}" : "")
                         + string.Concat(Enumerable.Range(0, _sim.State.Players.Count).Where(p => _sim.FeedsPostOf(p, e.Id, e.Index)).Select(p => $", cutting off {PlayerPalette.Name(p)}")));
                break;
            case SimEventKind.SegmentRepaired: GD.Print($"[{t}] belt {e.Id} segment {e.Index} repaired"); break;
        }
    }

    // `godot res://scenes/prototype.tscn -- --demo`: the prototype map (only), as a scripted match. Blue
    // raids Red's road beside Red's depot: its artillery shells a piece (only splash breaks road) while the
    // rest go there, killing the depot on the way (fire on the move), then shoot the truck held up at the
    // break and collect what it spills. Blue pulls back (its vehicles back up, then turn
    // round) while Red stops training squads to pay its builder to repair the break, and its vehicles break Blue's road; Red goes for Blue's
    // post, turrets swinging onto it on the way; then Blue attack-moves
    // into Red's side and they fight it out. Meanwhile each HQ trains a builder, which puts up a barracks
    // beside the HQ, and the barracks trains squads in batches of 20, as income allows; they gather at its rally
    // point. To show the ending, scripted kills finish Red: its buildings and posts at 85 s, and its
    // builders at 95 s. It's out once it can't rebuild: at 85 s if it can't afford a building then,
    // otherwise when its builders go.
    // The loaded truck on open road nearest a point, and where it is.
    (int Id, SVector3 At)? HeldTruck(SVector3 near)
    {
        (int, SVector3)? best = null;
        float bestSq = float.MaxValue;
        foreach (var line in _sim.State.Belts)
            foreach (var t in line.Packages)
                if (t.Cargo > 0 && !line.Segments[t.Segment].Covered && SVector3.DistanceSquared(t.Position, near) < bestSq)
                    (bestSq, best) = (SVector3.DistanceSquared(t.Position, near), (t.Id, t.Position with { Y = 0 }));
        return best;
    }

    void Demo(int tick)
    {
        const int T = Simulation.TicksPerSecond;
        const int Blue = 0, Red = 1;
        if (!GetTree().CurrentScene.SceneFilePath.EndsWith("prototype.tscn"))
        {
            if (tick == 0) GD.PushWarning("--demo is scripted for scenes/prototype.tscn.");
            return;
        }
        if (tick == 1)
            foreach (var hq in _sim.State.Buildings)
            {
                if (hq.Type.Id != "hq") continue;
                _commands.Add(new ProduceCommand(hq.Owner, hq.Id, "builder"));
                if (hq.Owner == PlayerInput.LocalPlayer) PlayerInput.SelectedBuilding = hq.Id; // shows the command card
            }
        if (tick == 12 * T)
            foreach (var unit in _sim.State.Units)
                if (unit.Builds is not null && _sim.State.Buildings.Find(b => b.Owner == unit.Owner) is { } hq)
                    _commands.Add(new BuildCommand(unit.Owner, unit.Id, "barracks", hq.Position + new SVector3(-8, 0, 0), hq.Heading));
        if (tick % T == 0) // a finished barracks with nothing queued: a batch of squads there
            foreach (var barracks in _sim.State.Buildings)
                if (barracks is { Built: true, Type.Id: "barracks", Queue.Count: 0 } && !(barracks.Owner == Red && tick >= 40 * T))
                    _commands.Add(new ProduceCommand(barracks.Owner, barracks.Id, "rifle_squad", Count: 20));

        if (tick == 2 * T)
        {
            MoveAll(Blue, new SVector3(27, 0, 9));                              // by the break to come...
            SegmentOrder(Blue, new SVector3(28, 0, 11), repair: false);         // ...which only the artillery can make (splash): Red's road, just upstream of its post
        }
        if (tick == 32 * T && HeldTruck(new SVector3(28, 0, 11)) is var (truck, at))    // the truck waiting there: shoot it, then pick up its load
        {
            foreach (int id in _unitsOf[Blue]) _commands.Add(new AttackCommand(Blue, id, truck));
            MoveAll(Blue, at, queued: true);
        }
        if (tick == 44 * T) // the first packages reach the break at about 39 s
        {
            MoveAll(Blue, new SVector3(28, 0, -2));                // close behind: vehicles back up...
            MoveAll(Blue, new SVector3(12, 0, -21), queued: true); // ...then far: they turn round
        }
        if (tick == 40 * T)
        {
            SegmentOrder(Red, new SVector3(28, 0, 11), repair: true);
            foreach (var barracks in _sim.State.Buildings) // Red stops training to afford the repair
                if (barracks is { Owner: Red, Type.Id: "barracks" })
                {
                    for (int i = barracks.Queue.Count - 1; i >= 0; i--) _commands.Add(new CancelProductionCommand(Red, barracks.Id, i));
                }
            SegmentOrder(Red, new SVector3(0, 0, -11), repair: false, vehiclesOnly: true);
        }
        if (tick == 56 * T && _sim.State.Gatherers.FirstOrDefault(g => g.Owner == Blue) is { Id: > 0 } post)
            foreach (int id in _unitsOf[Red])
                if (_sim.State.Units.Find(u => u.Id == id) is { Current.Kind: not UnitOrder.AttackSegment }) // the artillery keeps shelling the road
                    _commands.Add(new AttackCommand(Red, id, post.Id));
        if (tick == 80 * T) MoveAll(Blue, ToSim(new Vector3(HomeOf(Red).X, 0, HomeOf(Red).Y)), attack: true);
        if (tick == 85 * T)
        {
            foreach (var b in _sim.State.Buildings) if (b.Owner == Red) _commands.Add(new DestroyCommand(b.Id));
            foreach (var g in _sim.State.Gatherers) if (g.Owner == Red) _commands.Add(new DestroyCommand(g.Id));
        }
        if (tick == 95 * T)
            foreach (var u in _sim.State.Units) if (u.Owner == Red && u.Builds is not null) _commands.Add(new DestroyCommand(u.Id));
    }

    // The starting units of a side attack the belt segment at a point; or its units that repair (its
    // builders) repair it.
    void SegmentOrder(int player, SVector3 at, bool repair, bool squadsOnly = false, bool vehiclesOnly = false)
    {
        if (!_sim.FindSegment(at, 2, out int line, out int segment)) return;
        if (repair)
        {
            foreach (var u in _sim.State.Units)
                if (u.Owner == player && u.RepairSeconds > 0) _commands.Add(new RepairSegmentCommand(player, u.Id, line, segment));
            return;
        }
        foreach (int id in _unitsOf[player])
        {
            if (_sim.State.Units.FindIndex(u => u.Id == id) is not (>= 0 and var i)) continue;
            bool foot = _sim.State.Units[i].Movement == Movement.Foot;
            if ((squadsOnly && !foot) || (vehiclesOnly && foot)) continue;
            _commands.Add(new AttackSegmentCommand(player, id, line, segment));
        }
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
