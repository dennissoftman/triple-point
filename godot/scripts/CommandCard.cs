using System.Collections.Generic;
using Godot;
using Sim;

/// <summary>
/// The command card along the bottom of the screen, for what's selected:
/// - a building: a button per unit type it produces (cost, hotkey, how many are queued, progress on the
///   one in production) and a repeat toggle; left click queues one, right click takes one off;
/// - a foundation: how far along it is;
/// - builders: a button per building type they can put up; a click arms placement.
/// It issues nothing itself: it goes through PlayerInput, like the hotkeys. Placeholder UI.
/// </summary>
public partial class CommandCard : PanelContainer
{
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public int QueueLength; // as shown; for tools
    const float MinimapClearance = 126; // px: half the minimap's width and margin

    readonly List<Button> _buttons = [];
    readonly List<string> _shown = [], _tips = [];
    HBoxContainer _row = null!;
    CheckBox _repeat = null!;
    Label _help = null!;
    string _layout = ""; // what the buttons were built for

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0.5f, 0.5f, 1, 1);
        (GrowHorizontal, GrowVertical) = (GrowDirection.Both, GrowDirection.Begin);
        (OffsetLeft, OffsetRight, OffsetBottom) = (MinimapClearance, MinimapClearance, -12); // centered right of the minimap
        var column = new VBoxContainer();
        AddChild(column);
        column.AddChild(_row = new HBoxContainer());
        var footer = new HBoxContainer();
        column.AddChild(footer);
        footer.AddChild(_repeat = new CheckBox
        {
            Text = "Repeat",
            TooltipText = $"{KeyOf("produce_repeat")}: each finished unit goes back to the end of the queue, so a mix keeps its ratio",
            FocusMode = FocusModeEnum.None,
        });
        _repeat.Toggled += _ => PlayerInput.ToggleRepeat();
        footer.AddChild(_help = new Label());
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (PlayerInput.Building is Building building) ShowBuilding(building);
        else if (PlayerInput.Buildable is { Count: > 0 } types) ShowBuilds(types);
        else Visible = false;
    }

    void ShowBuilding(Building building)
    {
        Visible = true;
        _repeat.Visible = building.Built && building.Type.Units.Length > 0;
        if (!building.Built)
        {
            Layout($"site:{building.Id}", 0, "");
            string state = building.BuildStalled ? "stalled: not enough Resources" : building.WorkedTick >= PlayerInput.Host.Sim.State.Tick - 1 ? "building" : "waiting for a builder";
            SetHelp($"{Title(building.Type.Id)} {100 * building.BuildProgress / building.Type.BuildTicks}%, {state}");
            QueueLength = 0;
            return;
        }

        var types = building.Type.Units;
        Layout($"building:{building.Id}", types.Length, "");
        for (int i = 0; i < _buttons.Count; i++)
        {
            var type = types[i];
            int queued = 0;
            foreach (var t in building.Queue) if (t == type) queued++;
            string state = building.Queue.Count > 0 && building.Queue[0] == type
                ? building.Stalled ? "  stalled" : $"  {100 * building.Progress / type.BuildTicks}%"
                : "";
            string key = KeyOf(PlayerInput.SlotActions[i]);
            SetText(i, $"{Title(type.Id)}  [{key}]\n{type.Cost}" + (queued > 0 ? $"\nqueued {queued}{state}" : "\n"));
            SetTip(i, $"{Title(type.Id)}: {type.Cost} Resources, {type.BuildTime:0} s, paid as it trains\n"
                + $"{key} or click: train one   RMB: cancel one   {KeyOf("cancel_production")}: cancel the last\nRMB the ground: set the rally point");
        }
        if (_repeat.ButtonPressed != building.Repeat) _repeat.SetPressedNoSignal(building.Repeat);
        SetHelp("");
        QueueLength = building.Queue.Count;
    }

    void ShowBuilds(IReadOnlyList<BuildingType> types)
    {
        Visible = true;
        _repeat.Visible = false;
        Layout("builds:" + string.Join(",", Ids(types)), types.Count, "build");
        for (int i = 0; i < _buttons.Count; i++)
        {
            var type = types[i];
            string armed = PlayerInput.PlacingType == type.Id ? "  placing" : "";
            int short_ = type.Cost - PlayerInput.Host.Sim.State.Players[PlayerInput.LocalPlayer].Resources;
            string cost = short_ > 0 ? $"{type.Cost} ({short_} short)" : $"{type.Cost}";
            string key = KeyOf(PlayerInput.SlotActions[i]);
            SetText(i, $"{Title(type.Id)}  [{key}]\n{cost}{armed}");
            SetTip(i, $"{Title(type.Id)}: {type.Cost} Resources, {type.BuildTime:0} s. {Describe(type)}\n"
                + "Starting one needs the whole cost in hand; it's paid as it grows\n"
                + $"{key} or click: place it   Shift: place more   {KeyOf("rotate_building")}: rotate   RMB/Esc: cancel\n"
                + "RMB your unfinished foundation: build it");
        }
        SetHelp(PlayerInput.Placing is { Problem: string p } ? $"Can't place: {p}." : "");
        QueueLength = 0;
    }

    // Rebuilds the buttons when what they're for changes: `count` of them, each queueing a unit (at a
    // building) or arming placement (for builders).
    void Layout(string layout, int count, string mode)
    {
        if (layout == _layout) return;
        _layout = layout;
        foreach (var button in _buttons) button.QueueFree();
        _buttons.Clear();
        _shown.Clear();
        _tips.Clear();
        for (int i = 0; i < count; i++)
        {
            int slot = i;
            var button = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(150, 0) };
            if (mode == "build")
            {
                button.Pressed += () => { if (slot < PlayerInput.Buildable.Count) PlayerInput.StartPlacement(PlayerInput.Buildable[slot].Id); };
            }
            else
            {
                button.Pressed += () => { if (PlayerInput.Building is { } b && slot < b.Type.Units.Length) PlayerInput.Produce(b.Type.Units[slot].Id); };
                button.GuiInput += e =>
                {
                    if (e.IsActionPressed("act") && PlayerInput.Building is { } b && slot < b.Type.Units.Length) PlayerInput.CancelLast(b.Type.Units[slot].Id);
                };
            }
            _row.AddChild(button);
            _buttons.Add(button);
            _shown.Add("");
            _tips.Add("");
        }
    }

    void SetText(int i, string text)
    {
        if (_shown[i] != text) _buttons[i].Text = _shown[i] = text;
    }

    void SetTip(int i, string text)
    {
        if (_tips[i] != text) _buttons[i].TooltipText = _tips[i] = text;
    }

    void SetHelp(string text)
    {
        if (_help.Text != text) _help.Text = text;
    }

    static IEnumerable<string> Ids(IReadOnlyList<BuildingType> types)
    {
        foreach (var t in types) yield return t.Id;
    }

    static string Describe(BuildingType type) => type.Kind switch
    {
        BuildingKind.Post => "beside a belt",
        BuildingKind.Defense => "defense",
        _ => type.Units.Length > 0 ? "trains " + string.Join(", ", System.Linq.Enumerable.Select(type.Units, u => Title(u.Id).ToLowerInvariant())) : "",
    };

    // "rifle_squad" -> "Rifle squad"
    public static string Title(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' ');

    // The key an Input Map action is bound to, as text.
    /// <summary>What the first input bound to an action is called, short: "Q", "Ctrl", "RMB".</summary>
    public static string KeyOf(string action)
    {
        var events = InputMap.ActionGetEvents(action);
        return events.Count == 0 ? "?" : events[0].AsText().Replace(" (Physical)", "")
            .Replace("Left Mouse Button", "LMB").Replace("Right Mouse Button", "RMB").Replace("Middle Mouse Button", "MMB");
    }
}
