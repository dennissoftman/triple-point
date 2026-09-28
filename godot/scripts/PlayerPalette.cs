using Godot;

/// <summary>How players look in the prototype. Names and colors are presentation, so they live here, not in the sim.</summary>
public static class PlayerPalette
{
    static readonly Color[] Colors = [new(0.2f, 0.45f, 0.9f), new(0.85f, 0.25f, 0.2f)];
    static readonly string[] Names = ["Blue", "Red"];
    static readonly Color Neutral = new(0.6f, 0.6f, 0.62f);

    public static Color Color(int player) => player >= 0 && player < Colors.Length ? Colors[player] : Neutral;
    public static string Name(int player) => player >= 0 && player < Names.Length ? Names[player] : "Neutral";
}
