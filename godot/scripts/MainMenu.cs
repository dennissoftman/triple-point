using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim.Ai;

/// <summary>
/// The game's front door (the project's main scene): Skirmish, Controls, Settings and Quit. Skirmish opens
/// the setup (your side; the opponent: the AI at a level, or hotseat) and Start loads the map with it
/// (MatchSetup). Controls lists the keys, read from the Input Map, and the game's few big ideas. Settings
/// sets the window (GameSettings), saved as it changes. `cancel` goes back a page. `-- --menu-page=setup`
/// (or controls, settings, multiplayer) opens on that page, for frame captures. `-- --start=red,normal`
/// (blue or red; easy, normal or hotseat) starts that match at once, the first time the menu opens: an
/// exported build can't be given a scene on the command line. Multiplayer hosts or joins a networked match
/// (NetSession); `-- --host=7777 [--side=red] [--net-delay=3]` hosts and starts as soon as someone joins,
/// `-- --join=127.0.0.1:7777` joins, for unattended two-machine runs (tools/net_test.py). Placeholder UI.
/// </summary>
public partial class MainMenu : Control
{
    [Export] public bool ApplySettings = true; // false keeps the window as it is (tools set this)

    readonly Dictionary<string, Control> _pages = [];
    int _side;
    bool _hotseat;
    AiLevel _level = AiLevel.Normal;
    Label _setupNote = null!, _title = null!, _netStatus = null!;
    LineEdit _port = null!, _address = null!;
    Button _netStart = null!;
    int _hostSide;
    bool _autoStart;                    // --host: start as soon as someone joins
    int _delay = NetSession.DefaultDelay;

    [Export] public string Page = "title"; // the page shown; for tools
    static bool _startedFromArgs;

    public override void _Ready()
    {
        GameSettings.Load();
        if (ApplySettings) GameSettings.Apply(GetTree().Root);
        if (MatchSetup.Current is { } last) (_side, _hotseat, _level) = (last.LocalPlayer, last.Hotseat, last.Level);

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = new Color(0.11f, 0.12f, 0.11f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });
        _title = MenuParts.Heading(L.T("menu.title"), 64);
        _title.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        _title.Position = new Vector2(_title.Position.X, 120);
        _title.GrowHorizontal = GrowDirection.Both;
        AddChild(_title);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 };
        AddChild(center);
        AddPage(center, "title", TitlePage());
        AddPage(center, "setup", SetupPage());
        AddPage(center, "controls", ControlsPage());
        AddPage(center, "settings", SettingsPage());
        AddPage(center, "multiplayer", MultiplayerPage());
        var start = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--menu-page="));
        Show(start is not null && _pages.ContainsKey(start["--menu-page=".Length..]) ? start["--menu-page=".Length..] : "title");

        if (!_startedFromArgs && OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--start=")) is string match)
        {
            _startedFromArgs = true;
            var parts = match["--start=".Length..].ToLowerInvariant().Split(',');
            _side = parts[0] == "red" ? 1 : 0;
            string opponent = parts.Length > 1 ? parts[1] : "normal";
            (_hotseat, _level) = (opponent == "hotseat", opponent == "easy" ? AiLevel.Easy : AiLevel.Normal);
            Callable.From(StartMatch).CallDeferred(); // not while the tree is still adding this scene
        }
        if (!_startedFromArgs && (Arg("--host=") ?? Arg("--join=")) is not null)
        {
            _startedFromArgs = true;
            _hostSide = Arg("--side=") == "red" ? 1 : 0;
            if (Arg("--net-delay=") is string delay) _delay = int.Parse(delay);
            Show("multiplayer");
            if (Arg("--host=") is string port) { _port.Text = port; _autoStart = true; Host(); }
            else { _address.Text = Arg("--join=")!; Join(); }
        }
    }

    static string? Arg(string prefix) => OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..];

    void AddPage(Control parent, string name, Control column)
    {
        var panel = MenuParts.Panel();
        panel.Name = name;
        panel.AddChild(column);
        parent.AddChild(panel);
        _pages[name] = panel;
    }

    void Show(string page)
    {
        Page = page;
        foreach (var (name, panel) in _pages) panel.Visible = name == page;
        _title.Visible = page == "title"; // the other pages have their own headings
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("cancel") && Page != "title") { Show("title"); GetViewport().SetInputAsHandled(); }
    }

    static VBoxContainer Column(int separation = 10)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", separation);
        return column;
    }

    Control TitlePage()
    {
        var column = Column();
        column.AddChild(MenuParts.Button(L.T("menu.skirmish"), () => Show("setup"), 240, "Skirmish"));
        column.AddChild(MenuParts.Button(L.T("menu.multiplayer"), () => Show("multiplayer"), 240, "Multiplayer"));
        column.AddChild(MenuParts.Button(L.T("menu.controls"), () => Show("controls"), 240, "Controls"));
        column.AddChild(MenuParts.Button(L.T("menu.settings"), () => Show("settings"), 240, "Settings"));
        column.AddChild(MenuParts.Button(L.T("menu.quit"), () => GetTree().Quit(), 240, "Quit"));
        return column;
    }

    // ---- Skirmish setup ----

    Control SetupPage()
    {
        var column = Column(14);
        column.AddChild(MenuParts.Heading(L.T("menu.skirmish")));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 12);
        column.AddChild(grid);

        grid.AddChild(new Label { Text = L.T("menu.side") });
        grid.AddChild(Choices(
            [("Blue", PlayerPalette.Name(0), _side == 0, () => _side = 0), ("Red", PlayerPalette.Name(1), _side == 1, () => _side = 1)],
            button => button.Name == "Blue" ? PlayerPalette.Color(0) : PlayerPalette.Color(1)));
        grid.AddChild(new Label { Text = L.T("menu.opponent") });
        grid.AddChild(Choices(
            [
                ("Easy", L.T("menu.ai_easy"), !_hotseat && _level == AiLevel.Easy, () => (_hotseat, _level) = (false, AiLevel.Easy)),
                ("Normal", L.T("menu.ai_normal"), !_hotseat && _level == AiLevel.Normal, () => (_hotseat, _level) = (false, AiLevel.Normal)),
                ("Hotseat", L.T("menu.hotseat"), _hotseat, () => _hotseat = true),
            ], null));

        _setupNote = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.7f) };
        column.AddChild(_setupNote);
        UpdateNote();

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        buttons.AddChild(MenuParts.Button(L.T("menu.back"), () => Show("title"), 150, "Back"));
        buttons.AddChild(MenuParts.Button(L.T("menu.start"), StartMatch, 150, "Start"));
        column.AddChild(buttons);
        return column;
    }

    // A row of toggle buttons, one of which is on; picking one runs its action.
    HBoxContainer Choices((string Name, string Text, bool On, System.Action Pick)[] options, System.Func<Button, Color>? tint)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var group = new ButtonGroup();
        foreach (var option in options)
        {
            var button = MenuParts.Button(option.Text, () => { option.Pick(); UpdateNote(); }, 140, option.Name);
            (button.ToggleMode, button.ButtonGroup, button.ButtonPressed) = (true, group, option.On);
            if (tint is not null) button.AddThemeColorOverride("font_pressed_color", tint(button));
            row.AddChild(button);
        }
        return row;
    }

    void UpdateNote()
    {
        if (_setupNote is null) return;
        _setupNote.Text = _hotseat
            ? L.T("menu.hotseat.tip", CommandCard.KeyOf("debug_swap_player"))
            : L.T("menu.vs_ai", PlayerPalette.Name(1 - _side));
    }

    void StartMatch()
    {
        // A fresh seed each match: the AI's choices and the sim's dice differ from game to game.
        MatchSetup.Current = new MatchSetup.Choice(_side, _hotseat, _level, GD.Randi() | 1);
        GetTree().ChangeSceneToFile(MatchSetup.MatchScene);
    }

    // ---- Multiplayer ----

    Control MultiplayerPage()
    {
        var column = Column(14);
        column.AddChild(MenuParts.Heading(L.T("menu.multiplayer")));
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 12);
        column.AddChild(grid);

        grid.AddChild(new Label { Text = L.T("menu.side_hosting") });
        grid.AddChild(Choices(
            [("HostBlue", PlayerPalette.Name(0), _hostSide == 0, () => _hostSide = 0), ("HostRed", PlayerPalette.Name(1), _hostSide == 1, () => _hostSide = 1)],
            button => button.Name == "HostBlue" ? PlayerPalette.Color(0) : PlayerPalette.Color(1)));
        grid.AddChild(new Control());

        grid.AddChild(new Label { Text = L.T("menu.port") });
        grid.AddChild(_port = new LineEdit { Name = "Port", Text = NetSession.DefaultPort.ToString(), CustomMinimumSize = new Vector2(360, 0) });
        grid.AddChild(MenuParts.Button(L.T("menu.host"), Host, 140, "Host"));

        grid.AddChild(new Label { Text = L.T("menu.address") });
        grid.AddChild(_address = new LineEdit { Name = "Address", PlaceholderText = L.T("menu.address.hint"), CustomMinimumSize = new Vector2(360, 0) });
        grid.AddChild(MenuParts.Button(L.T("menu.join"), Join, 140, "Join"));

        column.AddChild(_netStatus = new Label { Name = "NetStatus", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(620, 0) });

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        buttons.AddChild(MenuParts.Button(L.T("menu.back"), () => { NetSession.Instance.Reset(); Show("title"); }, 150, "Back"));
        buttons.AddChild(_netStart = MenuParts.Button(L.T("menu.start"), () => NetSession.Instance.StartMatch(_hostSide, _delay), 150, "NetStart"));
        column.AddChild(buttons);
        return column;
    }

    void Host()
    {
        if (!int.TryParse(_port.Text.Trim(), out int port) || port is < 1 or > 65535) port = NetSession.DefaultPort;
        NetSession.Instance.Host(port);
    }

    // "address", "address:port" or "[ipv6]:port".
    void Join()
    {
        var text = _address.Text.Trim();
        int port = NetSession.DefaultPort, colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int p)) (text, port) = (text[..colon], p);
        else if (text.StartsWith('[') && text.IndexOf("]:", System.StringComparison.Ordinal) is int end and > 0 && int.TryParse(text[(end + 2)..], out p))
            (text, port) = (text[1..end], p);
        if (text.Length == 0) return;
        NetSession.Instance.Join(text, port);
    }

    // The session's state, as it changes (and the host's auto-start, for tools).
    public override void _Process(double delta)
    {
        if (Page != "multiplayer") return;
        var net = NetSession.Instance;
        string port = _port.Text.Trim();
        var addresses = NetSession.LocalAddresses();
        _netStatus.Text = net.State switch
        {
            NetSession.Status.Hosting => L.T("net.hosting", port, addresses.Length > 0 ? string.Join(", ", addresses) : L.T("net.no_address")),
            NetSession.Status.Joining => L.T("net.joining", _address.Text.Trim()),
            NetSession.Status.Ready => net.IsHost ? L.T("net.ready.host") : L.T("net.ready.guest"),
            NetSession.Status.Failed => L.T(net.Problem.Key, net.Problem.Arg),
            _ => L.T("net.idle"),
        };
        _netStart.Visible = net.IsHost && net.State is NetSession.Status.Hosting or NetSession.Status.Ready;
        _netStart.Disabled = net.State != NetSession.Status.Ready;
        if (_autoStart && net.IsHost && net.State == NetSession.Status.Ready) { _autoStart = false; net.StartMatch(_hostSide, _delay); }
    }

    // ---- Controls ----

    Control ControlsPage()
    {
        var column = Column(8);
        column.Theme = new Theme { DefaultFontSize = 13 }; // a lot to fit on one page (the canvas is 1152 x 648)
        column.AddChild(MenuParts.Heading(L.T("menu.controls"), 24));
        var sides = new HBoxContainer();
        sides.AddThemeConstantOverride("separation", 32);
        column.AddChild(sides);

        var keys = new GridContainer { Columns = 2 };
        keys.AddThemeConstantOverride("h_separation", 16);
        keys.AddThemeConstantOverride("v_separation", 4);
        string K(string action) => CommandCard.KeyOf(action);
        (string Keys, string What)[] rows =
        [
            (K("select"), L.T("menu.ctl.select")),
            (K("act"), L.T("menu.ctl.act")),
            (K("queue_order"), L.T("menu.ctl.queue")),
            ($"{K("force_attack")}+{K("act")}", L.T("menu.ctl.force")),
            (K("attack_move"), L.T("menu.ctl.attack_move")),
            ($"{K("stop")} / {K("hold")}", L.T("menu.ctl.stop_hold")),
            (K("auto_retreat"), L.T("menu.ctl.retreat")),
            ($"{K("group_set_1")} / {K("group_1")}", L.T("menu.ctl.groups")),
            (K("idle_builder"), L.T("menu.ctl.idle")),
            (K("rotate_building"), L.T("menu.ctl.rotate")),
            (K("cancel_production"), L.T("menu.ctl.cancel_production")),
            ($"{K("camera_forward")} {K("camera_left")} {K("camera_back")} {K("camera_right")}\n{K("camera_grab")}", L.T("menu.ctl.camera")),
            ($"{K("speed_down")} / {K("speed_up")}", L.T("menu.ctl.speed")),
            (K("pause_menu"), L.T("menu.ctl.pause")),
            (K("debug_swap_player"), L.T("menu.ctl.swap")),
            (K("toggle_debug"), L.T("menu.ctl.debug")),
        ];
        foreach (var (keyText, what) in rows)
        {
            keys.AddChild(new Label { Text = keyText, Modulate = new Color(1f, 0.85f, 0.5f) });
            keys.AddChild(new Label { Text = what, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) });
        }
        var left = Column(8);
        left.AddChild(MenuParts.Heading(L.T("menu.keys"), 20));
        left.AddChild(keys);
        left.AddChild(new Label { Text = L.T("menu.ctl.card"), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(470, 0) });
        sides.AddChild(left);

        var right = Column(10);
        right.AddChild(MenuParts.Heading(L.T("menu.ideas"), 20));
        foreach (var idea in new[]
        {
            L.T("menu.idea.trucks"),
            L.T("menu.idea.cut", $"{K("force_attack")}+{K("act")}"),
            L.T("menu.idea.roads"),
            L.T("menu.idea.armor"),
            L.T("menu.idea.houses"),
            L.T("menu.idea.end"),
        })
            right.AddChild(new Label { Text = "• " + idea, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(380, 0) });
        sides.AddChild(right);

        column.AddChild(Centered(MenuParts.Button(L.T("menu.back"), () => Show("title"), 150, "Back")));
        return column;
    }

    static CenterContainer Centered(Control control)
    {
        var center = new CenterContainer();
        center.AddChild(control);
        return center;
    }

    // ---- Settings ----

    Control SettingsPage()
    {
        var column = Column(14);
        column.AddChild(MenuParts.Heading(L.T("menu.settings")));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 24);
        grid.AddThemeConstantOverride("v_separation", 12);
        column.AddChild(grid);

        grid.AddChild(new Label { Text = L.T("menu.window") });
        var fullscreen = new CheckButton { Name = "Fullscreen", Text = L.T("menu.fullscreen"), ButtonPressed = GameSettings.Fullscreen, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        fullscreen.Toggled += on => { GameSettings.Fullscreen = on; Changed(); };
        grid.AddChild(fullscreen);

        grid.AddChild(new Label { Text = L.T("menu.ui_size") });
        string[] names = [L.T("menu.ui_compact"), L.T("menu.ui_normal")];
        var sizes = new (string, string, bool, System.Action)[GameSettings.UiSizes.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            float size = GameSettings.UiSizes[i];
            sizes[i] = ($"Size{i}", names[i], Mathf.IsEqualApprox(size, GameSettings.UiSize), () => { GameSettings.UiSize = size; Changed(); });
        }
        grid.AddChild(Choices(sizes, null));

        column.AddChild(Centered(MenuParts.Button(L.T("menu.back"), () => Show("title"), 150, "Back")));
        return column;
    }

    void Changed()
    {
        GameSettings.Save();
        if (ApplySettings) GameSettings.Apply(GetTree().Root);
    }
}
