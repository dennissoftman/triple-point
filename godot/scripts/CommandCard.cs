using System;
using System.Collections.Generic;
using Godot;
using Sim;

/// <summary>
/// The command card in the bottom-right corner: a 4 x 3 grid whose keys are where its buttons sit on the
/// keyboard (QWER / ASDF / ZXCV, physical keys, so the same places on any layout). The top row is what to
/// make: a building's unit types (cost, how many are queued, progress on the one in production; left click
/// queues one, right click takes one off) or what the selected builders can put up (a click arms
/// placement), or for a garrison building of yours, Exit. The middle row is the unit commands: attack-move,
/// stop, hold position. The bottom row's Z turns a building being placed, and X toggles auto-retreat. A line
/// above the grid says why a placement can't go. It issues nothing itself: it goes through PlayerInput, like the keys. Placeholder UI.
/// </summary>
public partial class CommandCard : PanelContainer
{
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public int QueueLength; // as shown; for tools
    public const float Margin = 12, CellWidth = 76, CellHeight = 44, Gap = 4;
    const int Columns = 4, Rows = 3;

    // Each cell's key, by position; null where nothing is bound yet.
    static readonly string?[] CellActions =
    [
        "slot_1", "slot_2", "slot_3", "slot_4",
        "attack_move", "stop", "hold", "slot_5",
        "rotate_building", "auto_retreat", null, null,
    ];
    const int AttackMoveCell = 4, StopCell = 5, HoldCell = 6, ZCell = 8, RetreatCell = 9;
    static readonly int[] SlotCells = [0, 1, 2, 3, 7]; // where slot_1..slot_5 sit: the top row, then F

    readonly Button[] _cells = new Button[Columns * Rows];
    readonly Label[] _keys = new Label[Columns * Rows], _badges = new Label[Columns * Rows];
    readonly ProgressBar[] _bars = new ProgressBar[Columns * Rows];
    readonly StyleBoxFlat[] _barFills = new StyleBoxFlat[Columns * Rows];
    readonly (string Badge, float Progress, bool Stalled)[] _extraShown = new (string, float, bool)[Columns * Rows];
    readonly (string Badge, float Progress, bool Stalled)[] _extraWant = new (string, float, bool)[Columns * Rows];
    // What each cell should show this frame, and what it shows (kept here, so only changes reach the engine).
    readonly (string Text, string Tip, string Key, bool Enabled, bool On)[] _want = new (string, string, string, bool, bool)[Columns * Rows];
    readonly (string Text, string Tip, string Key, bool Enabled, bool On)[] _shown = new (string, string, string, bool, bool)[Columns * Rows];
    readonly Action?[] _press = new Action?[Columns * Rows];
    readonly Action?[] _cancel = new Action?[Columns * Rows]; // a right click on the cell
    Label _help = null!;
    bool _visible;

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (1, 1, 1, 1);
        (GrowHorizontal, GrowVertical) = (GrowDirection.Begin, GrowDirection.Begin);
        (OffsetRight, OffsetBottom) = (-Margin, -Margin);
        AddThemeStyleboxOverride("panel", Panel());
        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(column);
        column.AddChild(_help = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(Columns * CellWidth + (Columns - 1) * Gap, 0),
            LabelSettings = new LabelSettings { FontSize = 11, FontColor = new Color(1, 0.6f, 0.45f) },
            Visible = false,
        });
        var grid = new GridContainer { Columns = Columns };
        grid.AddThemeConstantOverride("h_separation", (int)Gap);
        grid.AddThemeConstantOverride("v_separation", (int)Gap);
        column.AddChild(grid);
        for (int i = 0; i < _cells.Length; i++)
        {
            int cell = i;
            var button = new Button
            {
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(CellWidth, CellHeight),
                ClipText = true, // text never widens it
                ToggleMode = true,
                ClipContents = true,
            };
            button.AddThemeFontSizeOverride("font_size", 11);
            foreach (string look in new[] { "normal", "hover", "pressed", "disabled", "hover_pressed" })
                if (button.GetThemeStylebox(look).Duplicate() is StyleBox box)
                {
                    (box.ContentMarginLeft, box.ContentMarginRight, box.ContentMarginTop, box.ContentMarginBottom) = (3, 3, 8, 4); // room for the key and the bar
                    button.AddThemeStyleboxOverride(look, box);
                }
            button.Pressed += () =>
            {
                button.SetPressedNoSignal(_shown[cell].On); // a toggle's look follows the state, not the click
                _press[cell]?.Invoke();
            };
            button.GuiInput += e => { if (e.IsActionPressed("act")) _cancel[cell]?.Invoke(); };
            var key = new Label
            {
                Position = new Vector2(4, 1),
                MouseFilter = MouseFilterEnum.Ignore,
                LabelSettings = new LabelSettings { FontSize = 9, FontColor = new Color(1, 0.85f, 0.4f) },
            };
            button.AddChild(key);
            var badge = new Label // how many are queued, top right
            {
                AnchorLeft = 1, AnchorRight = 1, OffsetLeft = -36, OffsetRight = -4, OffsetTop = 1, // room for ×999
                HorizontalAlignment = HorizontalAlignment.Right,
                MouseFilter = MouseFilterEnum.Ignore,
                LabelSettings = new LabelSettings { FontSize = 9, FontColor = Colors.White, OutlineSize = 2, OutlineColor = Colors.Black },
            };
            button.AddChild(badge);
            var bar = new ProgressBar // the one in production, along the bottom
            {
                AnchorTop = 1, AnchorBottom = 1, AnchorRight = 1, OffsetLeft = 3, OffsetRight = -3, OffsetTop = -5, OffsetBottom = -2,
                ShowPercentage = false, MaxValue = 1, Step = 0, Visible = false, MouseFilter = MouseFilterEnum.Ignore,
            };
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f) });
            bar.AddThemeStyleboxOverride("fill", _barFills[i] = new StyleBoxFlat { BgColor = Progressing });
            button.AddChild(bar);
            grid.AddChild(button);
            (_cells[i], _keys[i], _badges[i], _bars[i]) = (button, key, badge, bar);
            _extraShown[i] = ("", -1, false);
            _shown[i] = ("", "", "", true, false); // as a new button is
            Apply(i, ("", "", "", false, false));
        }
        Visible = false;
    }

    // A dark panel, like the others in the bottom corners.
    public static StyleBoxFlat Panel() => new()
    {
        BgColor = new Color(0.08f, 0.09f, 0.1f, 0.85f),
        CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
        ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    public override void _Process(double delta)
    {
        var building = PlayerInput.Building;
        bool units = PlayerInput.Selection.Count > 0;
        bool show = building is not null || units;
        if (show != _visible) Visible = _visible = show;
        if (!show) return;
        for (int i = 0; i < _cells.Length; i++) Clear(i);
        string help = "";
        QueueLength = 0;

        if (building is { Type.Kind: BuildingKind.Garrison })
            Set(SlotCells[0], L.T("card.exit"), L.T("card.exit.tip"), PlayerInput.ExitGarrison, enabled: building.Occupants > 0);
        else if (building is { Built: true }) ShowProduction(building);
        else if (units)
        {
            var builds = PlayerInput.Buildable;
            for (int i = 0; i < builds.Count && i < SlotCells.Length; i++) ShowBuild(SlotCells[i], builds[i]);
            Set(AttackMoveCell, L.T("card.attack_move"), L.T("card.attack_move.tip"), PlayerInput.ArmAttackMove, pressed: PlayerInput.AttackMoveArmed);
            Set(StopCell, L.T("card.stop"), L.T("card.stop.tip"), () => PlayerInput.Halt(hold: false));
            Set(HoldCell, L.T("card.hold"), L.T("card.hold.tip"), () => PlayerInput.Halt(hold: true));
            bool retreats = PlayerInput.SelectionRetreats;
            Set(RetreatCell, L.T(retreats ? "card.retreat.on" : "card.retreat.off"), L.T("card.retreat.tip", (100 * Simulation.RetreatHealth).ToString("0")),
                PlayerInput.ToggleRetreat, pressed: retreats);
            if (PlayerInput.PlacingType is not null)
            {
                Set(ZCell, L.T("card.rotate"), L.T("card.rotate.tip"), PlayerInput.RotatePlacement);
                if (PlayerInput.Placing is { Problem: string p }) help = L.T("card.cant_place", p);
            }
        }
        for (int i = 0; i < _cells.Length; i++)
        {
            Apply(i, _want[i]);
            ApplyExtra(i, _extraWant[i]);
        }
        if (_help.Text != help)
        {
            _help.Text = help;
            _help.Visible = help.Length > 0;
        }
    }

    static readonly Color Progressing = new(0.45f, 0.8f, 1f), StalledColor = new(1f, 0.4f, 0.3f);

    void ApplyExtra(int i, (string Badge, float Progress, bool Stalled) want)
    {
        var shown = _extraShown[i];
        if (shown.Badge != want.Badge) _badges[i].Text = want.Badge;
        if ((shown.Progress >= 0) != (want.Progress >= 0)) _bars[i].Visible = want.Progress >= 0;
        if (want.Progress >= 0 && shown.Progress != want.Progress) _bars[i].Value = want.Progress;
        if (shown.Stalled != want.Stalled) _barFills[i].BgColor = want.Stalled ? StalledColor : Progressing;
        _extraShown[i] = want;
    }

    void Apply(int i, (string Text, string Tip, string Key, bool Enabled, bool On) want)
    {
        var shown = _shown[i];
        if (shown.Text != want.Text) _cells[i].Text = want.Text;
        if (shown.Tip != want.Tip) _cells[i].TooltipText = want.Tip;
        if (shown.Key != want.Key) _keys[i].Text = want.Key;
        if (shown.Enabled != want.Enabled) _cells[i].Disabled = !want.Enabled;
        if (shown.On != want.On) _cells[i].SetPressedNoSignal(want.On);
        _shown[i] = want;
    }

    void ShowProduction(Building building)
    {
        var types = building.Type.Units;
        for (int i = 0; i < types.Length && i < Columns; i++)
        {
            var type = types[i];
            int queued = 0;
            foreach (var t in building.Queue) if (t == type) queued++;
            bool making = building.Queue.Count > 0 && building.Queue[0] == type;
            string id = type.Id;
            Set(i, L.T("card.cell", L.Unit(type.Id), type.Cost), L.T("card.train.tip", L.Unit(type.Id), type.Cost, type.BuildTime.ToString("0"), KeyOf(CellActions[i]!), KeyOf("cancel_production")),
                () => PlayerInput.Produce(id), cancel: () => PlayerInput.CancelLast(id), pressed: queued > 0);
            _extraWant[i] = (queued > 0 ? L.T("card.queued", Mathf.Min(queued, Simulation.MaxQueue)) : "", making ? (float)building.Progress / type.BuildTicks : -1, making && building.Stalled);
        }
        QueueLength = building.Queue.Count;
    }

    void ShowBuild(int i, BuildingType type)
    {
        int short_ = type.Cost - PlayerInput.Host.Sim.State.Players[PlayerInput.LocalPlayer].Packages;
        string cost = short_ > 0 ? L.T("card.short", type.Cost, short_) : $"{type.Cost}";
        string id = type.Id;
        string tip = L.T("card.build.tip", L.Building(type.Id), type.Cost, type.BuildTime.ToString("0"), Describe(type), KeyOf(CellActions[i]!), KeyOf("rotate_building"));
        bool ready = PlayerInput.Host.Sim.HasRequired(PlayerInput.LocalPlayer, type);
        if (!ready) tip = L.T("card.requires", L.Building(type.Requires!)) + "\n" + tip;
        Set(i, L.T("card.cell", L.Building(type.Id), cost), tip, () => PlayerInput.StartPlacement(id), pressed: PlayerInput.PlacingType == type.Id, enabled: ready);
    }

    void Clear(int i)
    {
        (_press[i], _cancel[i]) = (null, null);
        _want[i] = ("", "", "", false, false);
        _extraWant[i] = ("", -1, false);
    }

    // A live cell: its text, tooltip, what a click does (and a right click), whether it shows as on, and its key.
    void Set(int i, string text, string tip, Action press, Action? cancel = null, bool pressed = false, string? key = null, bool enabled = true)
    {
        (_press[i], _cancel[i]) = (enabled ? press : null, cancel);
        _want[i] = (text, tip, KeyOf(key ?? CellActions[i]!), enabled, pressed);
    }

    static string Describe(BuildingType type) => type.Kind switch
    {
        BuildingKind.Post => L.T("card.describe.post"),
        BuildingKind.Defense => L.T("card.describe.defense"),
        _ => type.Units.Length > 0 ? L.T("card.describe.trains", string.Join(", ", System.Linq.Enumerable.Select(type.Units, u => L.Unit(u.Id)))) : "",
    };

    /// <summary>
    /// What the first input bound to an action is called, short: "Q", "Ctrl", "RMB". A physical key is
    /// named as the keyboard in use labels that place.
    /// </summary>
    public static string KeyOf(string action)
    {
        if (!KeyNames.TryGetValue(action, out var name)) KeyNames[action] = name = NameKey(action);
        return name;
    }

    static readonly Dictionary<string, string> KeyNames = []; // bindings don't change while it runs

    static string NameKey(string action)
    {
        var events = InputMap.ActionGetEvents(action);
        if (events.Count == 0) return "?";
        if (events[0] is InputEventKey { PhysicalKeycode: not Key.None } physical)
        {
            var local = DisplayServer.KeyboardGetKeycodeFromPhysical(physical.PhysicalKeycode);
            string name = OS.GetKeycodeString(local != Key.None ? local : physical.PhysicalKeycode);
            return physical.CtrlPressed ? "Ctrl+" + name : name;
        }
        return events[0].AsText().Replace(" (Physical)", "")
            .Replace("Left Mouse Button", L.T("key.lmb")).Replace("Right Mouse Button", L.T("key.rmb")).Replace("Middle Mouse Button", L.T("key.mmb"));
    }
}
