using Godot;

/// <summary>
/// Player-facing text. Every string the player reads goes through here as a symbolic key into
/// godot/locale/*.po; English is en.po, also the fallback for any key another language lacks. Values
/// format with .NET placeholders ({0}, {1}). Unit and building names are keys too (unit.&lt;id&gt;,
/// building.&lt;id&gt;), so the game data stays language-free. LocaleTests checks that the keys used here and
/// en.po match, both ways. The debug text (F3) is a developer tool and stays English.
///
/// The language is the OS's by default; `--lang=xx` picks one and remembers it in user://settings.cfg.
/// </summary>
public static class L
{
    const string SettingsPath = "user://settings.cfg";

    public static string T(string key) => TranslationServer.Translate(key);

    public static string T(string key, params object[] args) => string.Format(TranslationServer.Translate(key), args);

    public static string Unit(string id) => T("unit." + id);

    public static string Building(string id) => T("building." + id);

    /// <summary>At startup: a `--lang=xx` argument (saved for next time), else the saved choice, else the OS's.</summary>
    public static void ApplyLanguage()
    {
        var settings = new ConfigFile();
        settings.Load(SettingsPath); // missing is fine
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--lang="))
            {
                settings.SetValue("general", "language", arg["--lang=".Length..]);
                settings.Save(SettingsPath);
            }
        if (settings.GetValue("general", "language", "").AsString() is { Length: > 0 } language) TranslationServer.SetLocale(language);
    }
}
