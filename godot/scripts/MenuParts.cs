using Godot;

/// <summary>The pieces the menus share: the dark rounded panel and a plain button. Placeholder UI.</summary>
public static class MenuParts
{
    public static PanelContainer Panel(float alpha = 0.92f, int margin = 40)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.1f, alpha),
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = 18, ContentMarginBottom = 22,
        });
        return panel;
    }

    public static Button Button(string text, System.Action pressed, float width = 180, string name = "")
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(width, 0) };
        if (name.Length > 0) button.Name = name;
        button.Pressed += pressed;
        return button;
    }

    public static Label Heading(string text, int size = 30) =>
        new() { Text = text, HorizontalAlignment = HorizontalAlignment.Center, LabelSettings = new LabelSettings { FontSize = size } };
}
