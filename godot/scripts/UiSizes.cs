using Godot;

/// <summary>
/// Room reserved for text that changes, so the interface never jumps as a number grows a digit: a label
/// (or button) is given the width of the widest text it's meant to show, measured in its own font.
/// </summary>
public static class UiSizes
{
    /// <summary>Sets `control`'s minimum width to fit `widest` (e.g. "8888") at `fontSize`, plus `extra` px.</summary>
    public static void Reserve(Control control, string widest, int fontSize, float extra = 0)
    {
        var font = control.GetThemeFont("font");
        float width = font.GetStringSize(widest, HorizontalAlignment.Left, -1, fontSize).X + extra;
        control.CustomMinimumSize = control.CustomMinimumSize with { X = Mathf.Ceil(width) };
    }
}
