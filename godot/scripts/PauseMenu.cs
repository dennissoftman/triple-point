using Godot;

/// <summary>
/// The pause menu: the sim stops (the camera and views still run), the world takes no input, and it offers
/// Resume, Restart (reloads the scene) and Quit. `pause_menu` opens or closes it; so does `cancel` when
/// there's nothing else to cancel (PlayerInput decides that). Placeholder UI.
/// </summary>
public partial class PauseMenu : Control
{
    [Export] public SimHost Host = null!;

    public bool Open => Visible;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop; // while open, clicks stop here, not in the world
        ProcessMode = ProcessModeEnum.Always;
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.35f), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 };
        AddChild(center);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, 0.92f),
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            ContentMarginLeft = 40, ContentMarginRight = 40, ContentMarginTop = 18, ContentMarginBottom = 22,
        });
        center.AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);
        column.AddChild(new Label { Text = L.T("menu.paused"), HorizontalAlignment = HorizontalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 30 } });
        column.AddChild(MenuButton(L.T("menu.resume"), () => SetOpen(false)));
        column.AddChild(MenuButton(L.T("menu.restart"), () => GetTree().ReloadCurrentScene()));
        column.AddChild(MenuButton(L.T("menu.quit"), () => GetTree().Quit()));
        Visible = false;
    }

    static Button MenuButton(string text, System.Action pressed)
    {
        var button = new Button { Text = text, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(180, 0) };
        button.Pressed += pressed;
        return button;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("pause_menu")) { SetOpen(!Open); GetViewport().SetInputAsHandled(); }
    }

    public void SetOpen(bool open)
    {
        Visible = open;
        Host.Paused = open;
    }
}
