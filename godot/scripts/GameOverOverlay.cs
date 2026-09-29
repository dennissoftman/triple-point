using Godot;
using Sim;

/// <summary>
/// The banner once the game is over: who won (or a draw), with Restart (reloads the scene) and Quit. The
/// world keeps running behind it, and only the banner itself takes clicks, so the camera still works.
/// Placeholder UI.
/// </summary>
public partial class GameOverOverlay : CenterContainer
{
    [Export] public SimHost Host = null!;
    [Export] public string Shown = ""; // the banner's text once shown; for tools

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); // anchors alone would leave it zero-sized in the corner
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        var state = Host.Sim.State;
        if (!state.GameOver || Visible) return;
        Shown = state.Winner == Player.None ? "Draw" : $"{PlayerPalette.Name(state.Winner)} wins";
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, 0.88f),
            BorderWidthBottom = 3,
            BorderColor = state.Winner == Player.None ? Colors.Gray : PlayerPalette.Color(state.Winner),
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            ContentMarginLeft = 40, ContentMarginRight = 40, ContentMarginTop = 18, ContentMarginBottom = 18,
        });
        AddChild(panel);
        var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);
        column.AddChild(new Label { Text = Shown, HorizontalAlignment = HorizontalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 36 } });
        column.AddChild(new Label { Text = $"after {state.Tick / Simulation.TicksPerSecond / 60}:{state.Tick / Simulation.TicksPerSecond % 60:00}", HorizontalAlignment = HorizontalAlignment.Center });
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 16);
        column.AddChild(buttons);
        var restart = new Button { Text = "Restart", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(110, 0) };
        restart.Pressed += () => GetTree().ReloadCurrentScene();
        var quit = new Button { Text = "Quit", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(110, 0) };
        quit.Pressed += () => GetTree().Quit();
        buttons.AddChild(restart);
        buttons.AddChild(quit);
        Visible = true;
    }
}
