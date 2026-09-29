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
    readonly List<string> _shown = [];
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
        footer.AddChild(_repeat = new CheckBox { Text = $"Repeat [{KeyOf("produce_repeat")}]", FocusMode = FocusModeEnum.None });
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
            SetHelp($"{Title(building.Type.Id)} under construction: {100 * building.BuildProgress / building.Type.BuildTicks}%, {state}   RMB it with a builder: build");
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
            SetText(i, $"{Title(type.Id)}\n{type.Cost} Resources  [{KeyOf(PlayerInput.SlotActions[i])}]" + (queued > 0 ? $"\nqueued {queued}{state}" : "\n"));
        }
        if (_repeat.ButtonPressed != building.Repeat) _repeat.SetPressedNoSignal(building.Repeat);
        SetHelp(types.Length > 0
            ? $"RMB a button: cancel one   [{KeyOf("cancel_production")}]: cancel the last   RMB the ground: rally point"
            : "");
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
            string cost = short_ > 0 ? $"{type.Cost} Resources ({short_} short)" : $"{type.Cost} Resources";
            SetText(i, $"{Title(type.Id)}\n{cost}  [{KeyOf(PlayerInput.SlotActions[i])}]\n{Describe(type)}{armed}");
        }
        string problem = PlayerInput.Placing is { Problem: string p } ? $"Can't place: {p}.   " : "";
        SetHelp(PlayerInput.PlacingType is null
            ? "RMB your foundation: build it   (starting a building needs its whole cost; it's paid as it grows)"
            : $"{problem}LMB: place   Shift: place more   [{KeyOf("rotate_building")}]: rotate   RMB/Esc: cancel");
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
        }
    }

    void SetText(int i, string text)
    {
        if (_shown[i] != text) _buttons[i].Text = _shown[i] = text;
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
    static string KeyOf(string action)
    {
        var events = InputMap.ActionGetEvents(action);
        return events.Count > 0 ? events[0].AsText().Replace(" (Physical)", "") : "?";
    }
}
