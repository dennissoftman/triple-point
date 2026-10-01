using Godot;
using Sim;

/// <summary>
/// The banner once the game is over: who won (or a draw), or how a networked match ended; with Restart
/// (reloads the scene, with the same setup; not when networked), Main menu and Quit. The world keeps
/// running behind it, and only the banner itself takes clicks, so the camera still works.
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

    Label? _title, _detail;
    StyleBoxFlat? _style;

    public override void _Process(double delta)
    {
        var state = Host.Sim.State;
        if (!state.GameOver || Visible) return;
        Shown = state.Winner == Player.None ? L.T("gameover.draw") : L.T("gameover.wins", PlayerPalette.Name(state.Winner));
        ShowEnd(Shown, L.T("gameover.after", $"{state.Tick / Simulation.TicksPerSecond / 60}:{state.Tick / Simulation.TicksPerSecond % 60:00}"),
            state.Winner == Player.None ? Colors.Gray : PlayerPalette.Color(state.Winner));
    }

    /// <summary>
    /// The banner with these words (the game's end, or a networked match's: out of sync, the other player
    /// gone); called again, it changes them. No Restart in a networked match.
    /// </summary>
    public void ShowEnd(string title, string detail, Color border)
    {
        Shown = title;
        if (_title is null)
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", _style = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.09f, 0.1f, 0.88f),
                BorderWidthBottom = 3,
                CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
                ContentMarginLeft = 40, ContentMarginRight = 40, ContentMarginTop = 18, ContentMarginBottom = 18,
            });
            AddChild(panel);
            var column = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            column.AddThemeConstantOverride("separation", 10);
            panel.AddChild(column);
            column.AddChild(_title = new Label { HorizontalAlignment = HorizontalAlignment.Center, LabelSettings = new LabelSettings { FontSize = 36 } });
            column.AddChild(_detail = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(520, 0) });
            var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            buttons.AddThemeConstantOverride("separation", 16);
            column.AddChild(buttons);
            if (!Host.Networked) buttons.AddChild(MenuParts.Button(L.T("menu.restart"), () => GetTree().ReloadCurrentScene(), 110));
            buttons.AddChild(MenuParts.Button(L.T("menu.to_menu"), () => GetTree().ChangeSceneToFile(MatchSetup.MenuScene), 110));
            buttons.AddChild(MenuParts.Button(L.T("menu.quit"), () => GetTree().Quit(), 110));
        }
        (_title.Text, _detail!.Text, _style!.BorderColor) = (title, detail, border);
        _detail.Visible = detail.Length > 0;
        Visible = true;
    }
}
