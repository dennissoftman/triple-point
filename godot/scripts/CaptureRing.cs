using System;
using Godot;

/// <summary>
/// A flat ring on the ground around a switch, at its capture radius: a faint full circle in the owner's
/// color marks the zone, and a thicker arc fills clockwise from 12 o'clock with capture progress, in the
/// capturer's color.
/// </summary>
public partial class CaptureRing : MeshInstance3D
{
    const int Steps = 72;             // per full circle
    const float ZoneWidth = 0.12f;    // m
    const float ProgressWidth = 0.4f; // m

    public float Radius = 4f; // m; set before the first Set()

    readonly ImmediateMesh _mesh = new();
    readonly StandardMaterial3D _zone = RingMaterial(), _progress = RingMaterial();
    (float progress, Color zone, Color arc)? _drawn;

    public CaptureRing()
    {
        Mesh = _mesh;
        CastShadow = ShadowCastingSetting.Off;
    }

    public void Set(float progress, Color zoneColor, Color progressColor)
    {
        if (_drawn == (progress, zoneColor, progressColor)) return;
        _drawn = (progress, zoneColor, progressColor);
        (_zone.AlbedoColor, _progress.AlbedoColor) = (zoneColor, progressColor);

        _mesh.ClearSurfaces();
        Arc(_zone, ZoneWidth, 1);
        if (progress > 0) Arc(_progress, ProgressWidth, progress);
    }

    // Clockwise seen from above, starting at 12 o'clock (-Z, the far side from the camera).
    void Arc(Material material, float width, float fraction)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(Steps * fraction));
        float inner = Radius - width / 2, outer = Radius + width / 2;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, material);
        for (int i = 0; i <= steps; i++)
        {
            float angle = Mathf.Tau * fraction * i / steps;
            var direction = new Vector3(MathF.Sin(angle), 0, -MathF.Cos(angle));
            _mesh.SurfaceAddVertex(direction * outer);
            _mesh.SurfaceAddVertex(direction * inner);
        }
        _mesh.SurfaceEnd();
    }

    static StandardMaterial3D RingMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
