using System;
using System.Collections.Generic;
using Godot;

/// <summary>What the mouse would do right now, shown as the cursor.</summary>
public enum CursorKind { Default, Move, Attack, AttackMove, Repair }

/// <summary>
/// Placeholder cursors, drawn in code the first time they're needed (listed in PLACEHOLDERS.md). Round
/// or symmetric ones (ring, crosshairs, flip arrows) click at their center; tools (wrench, flag) point
/// up-left like a Windows cursor and click at their top-left. Shapes are distance functions (pixels to
/// the shape's edge, negative inside), filled in color with a dark outline so they read on any ground.
/// </summary>
public static class Cursors
{
    const int Size = 32;
    static readonly Color Outline = new(0, 0, 0, 0.85f);
    static readonly Vector2 Center = new(Size / 2f, Size / 2f);
    static readonly Dictionary<CursorKind, (ImageTexture Image, Vector2 Hotspot)> Drawn = [];

    public static void Apply(CursorKind kind)
    {
        if (kind == CursorKind.Default)
        {
            Input.SetCustomMouseCursor(null);
            return;
        }
        if (!Drawn.TryGetValue(kind, out var cursor)) Drawn[kind] = cursor = Draw(kind);
        Input.SetCustomMouseCursor(cursor.Image, Input.CursorShape.Arrow, cursor.Hotspot);
    }

    /// <summary>
    /// Back to the system cursor, and drops the drawn textures. Call on shutdown: a static cache would
    /// otherwise hold textures past the renderer's own teardown.
    /// </summary>
    public static void Release()
    {
        Input.SetCustomMouseCursor(null);
        foreach (var (image, _) in Drawn.Values) image.Dispose();
        Drawn.Clear();
    }

    /// <summary>Writes every cursor side by side, at 4x, into one PNG: for checking the art.</summary>
    public static void SaveSheet(string path)
    {
        var kinds = Enum.GetValues<CursorKind>()[1..]; // Default is the system arrow
        var sheet = Image.CreateEmpty(Size * kinds.Length, Size, false, Image.Format.Rgba8);
        sheet.Fill(new Color(0.36f, 0.38f, 0.33f)); // the ground color
        for (int i = 0; i < kinds.Length; i++)
        {
            var (texture, _) = Draw(kinds[i]);
            sheet.BlendRect(texture.GetImage(), new Rect2I(0, 0, Size, Size), new Vector2I(i * Size, 0));
        }
        sheet.Resize(sheet.GetWidth() * 4, sheet.GetHeight() * 4, Image.Interpolation.Nearest);
        sheet.SavePng(path);
    }

    static (ImageTexture, Vector2) Draw(CursorKind kind)
    {
        var (hotspot, layers) = kind switch
        {
            // Go here: a ring with a dot.
            CursorKind.Move => (Center, Layers((p => MathF.Min(Ring(p, Center, 8, 2.5f), Disc(p, Center, 2.5f)), new Color(0.45f, 1, 0.5f)))),
            // Shoot this: a crosshair. Orange for attack-move, which shoots whatever it meets on the way.
            CursorKind.Attack => (Center, Layers((p => Crosshair(p, Center), new Color(1, 0.3f, 0.25f)))),
            CursorKind.AttackMove => (Center, Layers((p => Crosshair(p, Center), new Color(1, 0.65f, 0.15f)))),
            // Fix this: a wrench, jaws up-left where it grips, handle down to the right.
            _ => (WrenchGrip, Layers((Wrench, new Color(0.35f, 0.9f, 1)))),
        };

        var image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float nearest = float.MaxValue;
                var fill = Colors.Transparent;
                foreach (var (shape, color) in layers) // later layers draw over earlier ones
                {
                    float d = shape(p);
                    if (d <= 0) fill = color;
                    nearest = MathF.Min(nearest, d);
                }
                image.SetPixel(x, y, nearest <= 0 ? fill : nearest <= 1.5f ? Outline : Colors.Transparent);
            }
        return (ImageTexture.CreateFromImage(image), hotspot);
    }

    static (Func<Vector2, float>, Color)[] Layers(params (Func<Vector2, float>, Color)[] layers) => layers;

    static float Crosshair(Vector2 p, Vector2 c) => MathF.Min(MathF.Min(Ring(p, c, 9, 2.5f), Disc(p, c, 1.5f)), MathF.Min(
        MathF.Min(Segment(p, c + new Vector2(0, -14), c + new Vector2(0, -5), 2.5f), Segment(p, c + new Vector2(0, 5), c + new Vector2(0, 14), 2.5f)),
        MathF.Min(Segment(p, c + new Vector2(-14, 0), c + new Vector2(-5, 0), 2.5f), Segment(p, c + new Vector2(5, 0), c + new Vector2(14, 0), 2.5f))));

    // Point-symmetric about the center: one arrow right along the top, one left along the bottom.

    static readonly Vector2 WrenchHead = new(9, 9), WrenchGrip = new(5, 5);

    // An open-end wrench: a round head with a slot cut into it toward the upper left (subtracting a shape
    // is max(a, -b)), so the jaws open there, and a handle running down to the lower right.
    static float Wrench(Vector2 p)
    {
        float head = MathF.Max(p.DistanceTo(WrenchHead) - 7, -Segment(p, WrenchHead, WrenchHead + new Vector2(-8, -8), 5));
        float handle = Segment(p, new(12, 12), new(27, 27), 4.5f);
        return MathF.Min(head, handle);
    }

    static float Ring(Vector2 p, Vector2 c, float radius, float width) => MathF.Abs(p.DistanceTo(c) - radius) - width / 2;

    static float Disc(Vector2 p, Vector2 c, float radius) => p.DistanceTo(c) - radius;

    static float Segment(Vector2 p, Vector2 a, Vector2 b, float width)
    {
        var ab = b - a;
        float t = Math.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t) - width / 2;
    }

    static float Triangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) => Polygon(p, [a, b, c]);

    // Distance to the nearest edge, negative inside (even-odd rule).
    static float Polygon(Vector2 p, Vector2[] points)
    {
        float edge = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            var (a, b) = (points[j], points[i]);
            edge = MathF.Min(edge, Segment(p, a, b, 0));
            if ((b.Y > p.Y) != (a.Y > p.Y) && p.X < (a.X - b.X) * (p.Y - b.Y) / (a.Y - b.Y) + b.X) inside = !inside;
        }
        return inside ? -edge : edge;
    }
}
