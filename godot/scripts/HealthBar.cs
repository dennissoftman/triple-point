using Godot;

/// <summary>A small camera-facing bar that fills from the left, drawn on top of the scene.</summary>
public partial class HealthBar : Node3D
{
    const float Width = 1.8f, Height = 0.22f, Border = 0.06f; // m; readable from the default zoom

    readonly QuadMesh _fill = new() { Size = new Vector2(Width, Height) };
    readonly StandardMaterial3D _fillMaterial = BarMaterial(Colors.White, renderPriority: 1);
    float _fraction = 1;

    public HealthBar()
    {
        AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(Width + Border, Height + Border) },
            MaterialOverride = BarMaterial(new Color(0, 0, 0, 0.65f), renderPriority: 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        AddChild(new MeshInstance3D { Mesh = _fill, MaterialOverride = _fillMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>Green when healthy, yellow when hurt, red when nearly dead.</summary>
    public static Color HealthColor(float fraction) =>
        fraction > 0.6f ? new Color(0.35f, 0.85f, 0.35f) : fraction > 0.3f ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.9f, 0.25f, 0.2f);

    /// <summary>Sets the fill (0..1) and its color; only touches the engine when something changed.</summary>
    public void Set(float fraction, Color color)
    {
        fraction = Mathf.Clamp(fraction, 0, 1);
        if (fraction != _fraction)
        {
            _fraction = fraction;
            _fill.Size = new Vector2(Width * fraction, Height);
            _fill.CenterOffset = new Vector3(-Width * (1 - fraction) / 2, 0, 0); // keep the left edge fixed
        }
        if (_fillMaterial.AlbedoColor != color) _fillMaterial.AlbedoColor = color;
    }

    static StandardMaterial3D BarMaterial(Color color, int renderPriority) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,
        RenderPriority = renderPriority,
        AlbedoColor = color,
    };
}
