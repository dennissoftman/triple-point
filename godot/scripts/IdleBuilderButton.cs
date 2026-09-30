using Godot;

/// <summary>
/// Just above the minimap, while any of your builders has nothing to do: how many, and a click (or the
/// idle_builder key) selects the next one and takes the camera to it. Placeholder UI.
/// </summary>
public partial class IdleBuilderButton : Button
{
    const float Gap = 6f; // px above the minimap
    public PlayerInput PlayerInput = null!;
    public Control? Minimap;

    int _shown = -1;

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.None;
        AddThemeFontSizeOverride("font_size", 12);
        UiSizes.Reserve(this, L.T("idle.builders", 88, CommandCard.KeyOf("idle_builder")), 12, extra: 20); // room for two digits
        ClipText = true;
        TooltipText = L.T("idle.tip", CommandCard.KeyOf("idle_builder"));
        Pressed += () => PlayerInput.SelectIdleBuilder();
        Visible = false;
    }

    public override void _Process(double delta)
    {
        int idle = PlayerInput.IdleBuilders;
        if (idle != _shown)
        {
            _shown = idle;
            Visible = idle > 0;
            Text = L.T("idle.builders", Mathf.Min(idle, 99), CommandCard.KeyOf("idle_builder"));
        }
        if (Minimap is not null && idle > 0) Position = new Vector2(Minimap.Position.X, Minimap.Position.Y - Size.Y - Gap);
    }
}
