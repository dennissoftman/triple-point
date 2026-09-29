using Godot;
using Sim;

/// <summary>
/// The local player's Resources, top center: the amount, large, and what they've earned per minute over
/// the last half minute (posts and pickups), which is what winning or losing the belt changes. Styled
/// in the player's color. Placeholder look.
/// </summary>
public partial class ResourcePanel : PanelContainer
{
    const int WindowSeconds = 30; // the income rate's window
    const float Margin = 10f;     // px from the top of the screen

    [Export] public SimHost Host = null!;
    [Export] public PlayerInput PlayerInput = null!;
    [Export] public int Shown = -1; // the amount as shown; for tools

    // Earned so far (gathered + collected) per player, once a sim second, for the last WindowSeconds + 1 seconds.
    readonly int[,] _earned = new int[8, WindowSeconds + 1];
    int _samples, _lastSampleTick = -1, _player = -1, _rate = int.MinValue;
    Label _amount = null!, _income = null!;
    StyleBoxFlat _style = null!;

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0.5f, 0.5f, 0, 0);
        (GrowHorizontal, GrowVertical) = (GrowDirection.Both, GrowDirection.End);
        OffsetTop = Margin;
        MouseFilter = MouseFilterEnum.Ignore;
        _style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, 0.82f),
            BorderWidthBottom = 3,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        AddThemeStyleboxOverride("panel", _style);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 14);
        AddChild(row);
        row.AddChild(new Label
        {
            Text = "RESOURCES",
            VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 13, FontColor = new Color(0.75f, 0.77f, 0.8f) },
        });
        row.AddChild(_amount = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 30, FontColor = new Color(1, 0.72f, 0.25f), OutlineSize = 2, OutlineColor = Colors.Black },
        });
        row.AddChild(_income = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 14, FontColor = new Color(0.8f, 0.82f, 0.85f) },
        });
    }

    public override void _Process(double delta)
    {
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
