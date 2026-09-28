using System.Collections.Generic;
using Godot;
using Sim;

/// <summary>
/// The production bar along the bottom of the screen while you have a building selected: a button per
/// unit type it produces (cost, hotkey, how many are queued, progress on the one in production), and a
/// repeat toggle. Left click queues one, right click takes the last one of that type off. It issues
/// nothing itself: it goes through PlayerInput, like the hotkeys. Placeholder UI.
/// </summary>
public partial class ProductionPanel : PanelContainer
{
    static readonly string[] ProduceActions = ["produce_1", "produce_2", "produce_3"];

    [Export] public PlayerInput PlayerInput = null!;
    [Export] public int QueueLength; // as shown; for tools

    readonly List<(Button Button, UnitType Type)> _buttons = [];
    readonly List<string> _shown = [];
    HBoxContainer _row = null!;
    CheckBox _repeat = null!;
    int _building = -1; // whose buttons are built

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0.5f, 0.5f, 1, 1);
        (GrowHorizontal, GrowVertical) = (GrowDirection.Both, GrowDirection.Begin);
        OffsetBottom = -12;
        var column = new VBoxContainer();
        AddChild(column);
        column.AddChild(_row = new HBoxContainer());
        var footer = new HBoxContainer();
        column.AddChild(footer);
        footer.AddChild(_repeat = new CheckBox { Text = $"Repeat [{KeyOf("produce_repeat")}]", FocusMode = FocusModeEnum.None });
        _repeat.Toggled += _ => PlayerInput.ToggleRepeat();
        footer.AddChild(new Label { Text = $"RMB a button: cancel one   [{KeyOf("cancel_production")}]: cancel the last   RMB the ground: rally point" });
        Visible = false;
    }

    public override void _Process(double delta)
    {
        var building = PlayerInput.Building;
        Visible = building is not null;
        if (building is null) return;
        if (building.Id != _building) BuildButtons(building);

        for (int i = 0; i < _buttons.Count; i++)
        {
            var (button, type) = _buttons[i];
            int queued = 0;
            foreach (var t in building.Queue) if (t == type) queued++;
            string state = building.Queue.Count > 0 && building.Queue[0] == type
                ? building.Stalled ? "  stalled" : $"  {100 * building.Progress / type.BuildTicks}%"
                : "";
            string text = $"{Title(type.Id)}\n{type.Cost} Resources  [{KeyOf(ProduceActions[i])}]" + (queued > 0 ? $"\nqueued {queued}{state}" : "\n");
            if (_shown[i] != text) button.Text = _shown[i] = text;
        }
        if (_repeat.ButtonPressed != building.Repeat) _repeat.SetPressedNoSignal(building.Repeat);
        QueueLength = building.Queue.Count;
    }

    void BuildButtons(Building building)
    {
        _building = building.Id;
        foreach (var (button, _) in _buttons) button.QueueFree();
        _buttons.Clear();
        _shown.Clear();
        for (int i = 0; i < building.Type.Units.Length && i < ProduceActions.Length; i++)
        {
            var type = building.Type.Units[i];
            var button = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(150, 0) };
            button.Pressed += () => PlayerInput.Produce(type.Id);
            button.GuiInput += e => { if (e.IsActionPressed("act")) PlayerInput.CancelLast(type.Id); };
            _row.AddChild(button);
            _buttons.Add((button, type));
            _shown.Add("");
        }
    }

    // "rifle_squad" -> "Rifle squad"
    static string Title(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' ');

    // The key an Input Map action is bound to, as text.
    static string KeyOf(string action)
    {
        var events = InputMap.ActionGetEvents(action);
        return events.Count > 0 ? events[0].AsText().Replace(" (Physical)", "") : "?";
    }
}
