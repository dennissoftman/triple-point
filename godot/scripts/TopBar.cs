using Godot;

/// <summary>
/// A slim strip across the top of the screen: the HUD line (game time, speed, your side) sits at its left,
/// your packages and income in the middle (PackagePanel), and alerts at its right, such as a road cut
/// upstream of one of your depots, pulsing while it's news. Placeholder look.
/// </summary>
public partial class TopBar : Control
{
    public const float Height = 30;
    public SimHost Host = null!;

    Label _alert = null!;
    bool _alerting;

    public override void _Ready()
    {
        (AnchorLeft, AnchorRight, AnchorTop, AnchorBottom) = (0, 1, 0, 0);
        (OffsetLeft, OffsetRight, OffsetTop, OffsetBottom) = (0, 0, 0, Height);
        MouseFilter = MouseFilterEnum.Stop; // the strip isn't the world
        AddChild(_alert = new Label
        {
            AnchorLeft = 1, AnchorRight = 1,
            GrowHorizontal = GrowDirection.Begin,
            OffsetRight = -16, OffsetTop = 5,
            Text = L.T("alert.belt_cut"),
            Visible = false,
            LabelSettings = new LabelSettings { FontSize = 14, FontColor = new Color(1, 0.45f, 0.35f), OutlineSize = 3, OutlineColor = Colors.Black },
        });
    }

    public override void _Draw() => DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.06f, 0.07f, 0.08f, 0.85f));

    public override void _Process(double delta)
    {
        bool alerting = Host.Alerts.Count > 0;
        if (alerting != _alerting) _alert.Visible = _alerting = alerting;
        if (alerting) _alert.Modulate = new Color(1, 1, 1, 0.6f + 0.4f * Mathf.Abs(Mathf.Sin((float)Time.GetTicksMsec() / 250f)));
    }
}
