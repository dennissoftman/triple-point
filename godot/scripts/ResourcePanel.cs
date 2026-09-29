using Godot;
using Sim;

/// <summary>
/// The local player's Resources, just above the minimap and as wide: the amount, and what they've earned
/// per minute over the last half minute (posts and pickups), which is what winning or losing the belt
/// changes. Styled in the player's color. Placeholder look.
/// </summary>
public partial class ResourcePanel : PanelContainer
{
    const int WindowSeconds = 30; // the income rate's window
    const float Gap = 6f;         // px above the minimap

    [Export] public SimHost Host = null!;
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public Control Minimap = null!;
    [Export] public int Shown = -1; // the amount as shown; for tools

    // Earned so far (gathered + collected) per player, once a sim second, for the last WindowSeconds + 1 seconds.
    readonly int[,] _earned = new int[8, WindowSeconds + 1];
    int _samples, _lastSampleTick = -1, _player = -1, _rate = int.MinValue;
    Label _amount = null!, _income = null!;
    StyleBoxFlat _style = null!;

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0, 0, 0, 0); // placed over the minimap in _Process
        MouseFilter = MouseFilterEnum.Pass; // for its tooltip
        TooltipText = "What you have to spend. Your posts gather packages off the belt, and your units pick up spilled ones.\n"
            + $"+N/min: what you earned per minute over the last {WindowSeconds} s";
        _style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, 0.82f),
            BorderWidthBottom = 3,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 3, ContentMarginBottom = 3,
        };
        AddThemeStyleboxOverride("panel", _style);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin, MouseFilter = MouseFilterEnum.Ignore }; // clicks go through to the world
        row.AddThemeConstantOverride("separation", 10);
        AddChild(row);
        row.AddChild(new Label
        {
            Text = "RESOURCES",
            VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 11, FontColor = new Color(0.75f, 0.77f, 0.8f) },
        });
        row.AddChild(_amount = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 20, FontColor = new Color(1, 0.72f, 0.25f), OutlineSize = 2, OutlineColor = Colors.Black },
        });
        row.AddChild(_income = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 12, FontColor = new Color(0.8f, 0.82f, 0.85f) },
        });
    }

    public override void _Process(double delta)
    {
        var place = new Vector2(Minimap.Position.X, Minimap.Position.Y - Size.Y - Gap);
        if (Position != place) Position = place;
        if (CustomMinimumSize.X != Minimap.Size.X) CustomMinimumSize = new Vector2(Minimap.Size.X, 0);

        var state = Host.Sim.State;
        Sample(state);
        int local = PlayerInput.LocalPlayer;
        if (local < 0 || local >= state.Players.Count) return;

        if (local != _player)
        {
            _player = local;
            _style.BorderColor = PlayerPalette.Color(local);
        }
        int amount = state.Players[local].Resources;
        if (amount != Shown) _amount.Text = (Shown = amount).ToString();
        int rate = Rate(local);
        if (rate != _rate) _income.Text = $"+{_rate = rate}/min";
    }

    // Once a sim second, every player's earnings so far, into a ring buffer.
    void Sample(SimState state)
    {
        int second = state.Tick / Simulation.TicksPerSecond;
        if (second == _lastSampleTick) return;
        _lastSampleTick = second;
        int slot = _samples++ % (WindowSeconds + 1);
        for (int p = 0; p < state.Players.Count && p < _earned.GetLength(0); p++)
            _earned[p, slot] = state.Players[p].Gathered + state.Players[p].Collected;
    }

    // Earned per minute across the samples held: up to WindowSeconds of them.
    int Rate(int player)
    {
        int held = Mathf.Min(_samples, WindowSeconds + 1);
        if (held < 2 || player >= _earned.GetLength(0)) return 0;
        int newest = (_samples - 1) % (WindowSeconds + 1), oldest = (_samples - held) % (WindowSeconds + 1);
        return (_earned[player, newest] - _earned[player, oldest]) * 60 / (held - 1);
    }
}
