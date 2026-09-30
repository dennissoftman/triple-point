using System.Collections.Generic;
using Godot;
using Sim;

/// <summary>
/// What's selected, bottom-center between the minimap and the command card.
/// - One unit: its name, health and what it's doing.
/// - Several: a tile per unit type with how many and their health (the group's, and its worst); a click
///   keeps only that type, select_add plus a click drops it.
/// - A building: its name and health, and what it's training (or, a foundation, how far along it is).
/// Placeholder UI: tiles are buttons with the type's name.
/// </summary>
public partial class SelectionPanel : PanelContainer
{
    public PlayerInput PlayerInput = null!;
    public const float Width = 470; // between the minimap and the command card at 1152 wide
    const float Height = 3 * CommandCard.CellHeight + 2 * CommandCard.Gap + 12; // as tall as the command card: its grid and panel margins
    const float TileWidth = 92, TileHeight = 34;
    const int MaxTiles = 10; // two rows; more unit types than that aren't in the game

    Label _title = null!, _detail = null!;
    public string Detail => _detailShown; // what the detail line says (the input smoke test reads it)
    ProgressBar _health = null!;
    StyleBoxFlat _healthFill = null!;
    HFlowContainer _tiles = null!;
    // A tile, and what it last showed (kept here, so only changes reach the engine).
    sealed class Tile(string type, Button button, ProgressBar bar, StyleBoxFlat fill)
    {
        public readonly string Type = type;
        public readonly Button Button = button;
        public readonly ProgressBar Bar = bar;
        public readonly StyleBoxFlat Fill = fill;
        public string Text = "", Tip = "";
        public float Health = -1;
    }
    readonly List<Tile> _tileViews = [];
    readonly Dictionary<string, (int Count, float Health, float Max, float Worst)> _groups = [];
    readonly List<string> _order = [];
    string _signature = "", _titleShown = "", _detailShown = "";
    float _healthShown = -1;
    bool _visible, _healthVisible = true;

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0.5f, 0.5f, 1, 1);
        (GrowHorizontal, GrowVertical) = (GrowDirection.Both, GrowDirection.Begin);
        (OffsetLeft, OffsetRight, OffsetBottom) = (-Width / 2, Width / 2, -CommandCard.Margin);
        CustomMinimumSize = new Vector2(Width, Height); // and its contents never outgrow it: it doesn't resize
        ClipContents = true;
        AddThemeStyleboxOverride("panel", CommandCard.Panel());
        MouseFilter = MouseFilterEnum.Stop; // clicks on it don't reach the world

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 4);
        AddChild(column);
        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddChild(header);
        header.AddChild(_title = new Label { ClipText = true, CustomMinimumSize = new Vector2(200, 0), LabelSettings = new LabelSettings { FontSize = 15, OutlineSize = 2, OutlineColor = Colors.Black } });
        header.AddChild(_health = Bar(out _healthFill, 180, 10));
        _health.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        column.AddChild(_detail = new Label
        {
            ClipText = true, // one line, whatever it says
            CustomMinimumSize = new Vector2(Width - 16, 0),
            LabelSettings = new LabelSettings { FontSize = 12, FontColor = new Color(0.82f, 0.84f, 0.87f) },
        });
        column.AddChild(_tiles = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
        Visible = _visible = false;
    }

    static ProgressBar Bar(out StyleBoxFlat fill, float width, float height)
    {
        var bar = new ProgressBar { ShowPercentage = false, MaxValue = 1, Step = 0, CustomMinimumSize = new Vector2(width, height), MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f) });
        bar.AddThemeStyleboxOverride("fill", fill = new StyleBoxFlat { BgColor = HealthBar.HealthColor(1) });
        return bar;
    }

    public override void _Process(double delta)
    {
        var state = PlayerInput.Host.Sim.State;
        var building = PlayerInput.Building;
        var selection = PlayerInput.Selection;
        bool show = building is not null || selection.Count > 0;
        if (_visible != show) Visible = _visible = show;
        if (!show) return;

        if (building is not null) ShowBuilding(state, building);
        else if (selection.Count == 1 && state.Units.Find(u => u.Id == selection[0]) is { Id: > 0 } unit) ShowUnit(unit);
        else ShowGroups(state, selection);
    }

    void ShowUnit(in Unit unit)
    {
        Show(TypeName(unit.Type), unit.Health / unit.MaxHealth, L.T("select.unit", unit.Health.ToString("0"), unit.MaxHealth.ToString("0"), Doing(unit)));
        Tiles("");
    }

    // What a unit is doing, in a word or two.
    static string Doing(in Unit unit)
    {
        string doing = unit.Holding ? L.T("order.hold") : unit.RespondTo >= 0 ? L.T("order.answering") : unit.Current.Kind switch
        {
            UnitOrder.Move => L.T("order.move"),
            UnitOrder.Attack => L.T("order.attack"),
            UnitOrder.AttackMove => L.T("order.attack_move"),
            UnitOrder.AttackSegment => L.T("order.attack_road"),
            UnitOrder.Repair => L.T("order.repair"),
            UnitOrder.Build => L.T("order.build"),
            _ => L.T("order.idle"),
        };
        return unit.Firing ? L.T("order.firing", doing) : doing;
    }

    void ShowGroups(SimState state, IReadOnlyList<int> selection)
    {
        _groups.Clear();
        _order.Clear();
        int total = 0;
        foreach (var u in state.Units)
        {
            if (!Contains(selection, u.Id)) continue;
            total++;
            float health = u.Health / u.MaxHealth;
            if (_groups.TryGetValue(u.Type, out var g)) _groups[u.Type] = (g.Count + 1, g.Health + u.Health, g.Max + u.MaxHealth, Mathf.Min(g.Worst, health));
            else
            {
                _groups[u.Type] = (1, u.Health, u.MaxHealth, health);
                _order.Add(u.Type);
            }
        }
        Show(L.T("select.units", total), -1, _groups.Count > 1 ? L.T("select.narrow", CommandCard.KeyOf("select_add")) : "");
        Tiles(string.Join(",", _order));
        foreach (var tile in _tileViews)
        {
            var g = _groups[tile.Type];
            string text = L.T("select.tile", TypeName(tile.Type), Mathf.Min(g.Count, 999));
            if (tile.Text != text) tile.Button.Text = tile.Text = text;
            string tip = L.T("select.tile.tip", TypeName(tile.Type), g.Count, (100 * g.Worst).ToString("0"));
            if (tile.Tip != tip) tile.Button.TooltipText = tile.Tip = tip;
            float health = g.Health / g.Max;
            if (tile.Health != health) SetBar(tile.Bar, tile.Fill, tile.Health = health);
        }
    }

    static bool Contains(IReadOnlyList<int> list, int id)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == id) return true;
        return false;
    }

    void ShowBuilding(SimState state, Building building)
    {
        string detail;
        if (!building.Built)
        {
            string how = L.T(building.BuildStalled ? "card.site.stalled" : building.WorkedTick >= state.Tick - 1 ? "card.site.building" : "card.site.waiting");
            detail = L.T("card.site", L.Building(building.Type.Id), 100 * building.BuildProgress / building.Type.BuildTicks, how);
        }
        else if (building.Queue.Count > 0)
        {
            var first = building.Queue[0];
            string progress = building.Stalled ? L.T("card.stalled") : $"{100 * building.Progress / first.BuildTicks}%";
            detail = L.T("select.training", L.Unit(first.Id), progress, building.Queue.Count - 1);
        }
        else detail = building.Type.Units.Length > 0 ? L.T("select.not_training") : "";
        Show(L.Building(building.Type.Id), building.Health / building.MaxHealth, detail);
        Tiles("");
    }

    void Show(string title, float health, string detail)
    {
        if (_titleShown != title) _title.Text = _titleShown = title;
        if (_detailShown != detail) _detail.Text = _detailShown = detail;
        if ((health >= 0) != _healthVisible) _health.Visible = _healthVisible = health >= 0;
        if (health >= 0 && health != _healthShown) SetBar(_health, _healthFill, _healthShown = health);
    }

    static void SetBar(ProgressBar bar, StyleBoxFlat fill, float health)
    {
        bar.Value = health;
        fill.BgColor = HealthBar.HealthColor(health);
    }

    // Rebuilds the tiles when the types selected change (signature: their ids in order).
    void Tiles(string signature)
    {
        if (signature == _signature) return;
        _signature = signature;
        foreach (var t in _tileViews) t.Button.GetParent().QueueFree();
        _signature = signature;
        _tileViews.Clear();
        if (signature.Length == 0) return;
        for (int k = 0; k < _order.Count && k < MaxTiles; k++)
        {
            string type = _order[k];
            var tile = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            tile.AddThemeConstantOverride("separation", 1);
            var button = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(TileWidth, TileHeight), ClipText = true, ClipContents = true };
            button.AddThemeFontSizeOverride("font_size", 11);
            string id = type;
            button.Pressed += () => PlayerInput.NarrowTo(id, drop: Input.IsActionPressed("select_add"));
            tile.AddChild(button);
            var bar = Bar(out var fill, TileWidth, 5);
            tile.AddChild(bar);
            _tiles.AddChild(tile);
            _tileViews.Add(new Tile(type, button, bar, fill));
        }
    }

    static string TypeName(string type) => type.Length > 0 ? L.Unit(type) : L.T("select.unnamed");
}
