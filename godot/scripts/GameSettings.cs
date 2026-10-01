using Godot;

/// <summary>
/// The player's window settings, kept in user://settings.cfg (beside the language, L): fullscreen or
/// windowed, and the interface's size on top of the stretch that already fits it to the window: Compact or
/// Normal (larger doesn't fit the 1152 x 648 canvas the panels and menus are laid out on). Applied when the
/// main menu opens and as they change there, so tools that run a scene directly keep their own window.
/// </summary>
public static class GameSettings
{
    const string FilePath = "user://settings.cfg", Section = "display";
    public static readonly float[] UiSizes = [0.85f, 1f]; // Compact, Normal

    public static bool Fullscreen;
    public static float UiSize = 1f;
    static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var file = new ConfigFile();
        if (file.Load(FilePath) != Error.Ok) return; // first run: the defaults
        Fullscreen = file.GetValue(Section, "fullscreen", false).AsBool();
        UiSize = file.GetValue(Section, "ui_size", 1f).AsSingle();
    }

    public static void Save()
    {
        var file = new ConfigFile();
        file.Load(FilePath); // keeps the other sections (the language); missing is fine
        file.SetValue(Section, "fullscreen", Fullscreen);
        file.SetValue(Section, "ui_size", UiSize);
        file.Save(FilePath);
    }

    public static void Apply(Window root)
    {
        var mode = Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed;
        if (DisplayServer.WindowGetMode() != mode) DisplayServer.WindowSetMode(mode);
        root.ContentScaleFactor = UiSize;
    }
}
