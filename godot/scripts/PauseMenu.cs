using Godot;

/// <summary>
/// The pause menu: the sim stops (the camera and views still run), the world takes no input, and it offers
/// Resume, Restart (reloads the scene, with the same setup), Main menu and Quit. `pause_menu` opens or closes it; so does `cancel` when
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
        var panel = MenuParts.Panel();
        center.AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);
        column.AddChild(MenuParts.Heading(L.T("menu.paused")));
        column.AddChild(MenuParts.Button(L.T("menu.resume"), () => SetOpen(false)));
        column.AddChild(MenuParts.Button(L.T("menu.restart"), () => GetTree().ReloadCurrentScene()));
        column.AddChild(MenuParts.Button(L.T("menu.to_menu"), () => GetTree().ChangeSceneToFile(MatchSetup.MenuScene), name: "ToMenu"));
        column.AddChild(MenuParts.Button(L.T("menu.quit"), () => GetTree().Quit()));
        Visible = false;
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
