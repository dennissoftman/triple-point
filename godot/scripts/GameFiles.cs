using System.IO;
using Godot;
using Sim;

/// <summary>
/// The game data files (/data: weapons, units, buildings), read for the sim and hashed for the network
/// handshake: two machines play only if their data is the same. In the editor they're in the repo beside
/// the project; an exported build has no project folder, so the export puts them beside the executable.
/// </summary>
public static class GameFiles
{
    public static readonly string[] Names = ["weapons.json", "units.json", "buildings.json"];

    public static string Folder(string dataDirectory = "../data") => OS.HasFeature("template")
        ? Path.Combine(OS.GetExecutablePath().GetBaseDir(), "data")
        : Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), dataDirectory));

    /// <summary>Each file's text, in Names order.</summary>
    public static string[] Read(string dataDirectory = "../data")
    {
        var folder = Folder(dataDirectory);
        var texts = new string[Names.Length];
        for (int i = 0; i < Names.Length; i++) texts[i] = File.ReadAllText(Path.Combine(folder, Names[i]));
        return texts;
    }

    /// <summary>A fingerprint of the files' contents (0 if they can't be read).</summary>
    public static ulong Hash()
    {
        try
        {
            var h = new StateHasher();
            foreach (var text in Read()) h.Add(text.Replace("\r\n", "\n")); // the same files checked out on Windows or not
            return h.Value;
        }
        catch (IOException) { return 0; }
    }

    /// <summary>This build: the sim's and the game's compiled code. Two machines play only on the same one.</summary>
    public static string Build() =>
        $"{typeof(Simulation).Assembly.ManifestModule.ModuleVersionId:N}/{typeof(GameFiles).Assembly.ManifestModule.ModuleVersionId:N}";
}
