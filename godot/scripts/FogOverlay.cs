using System;
using Godot;
using Sim;

/// <summary>
/// The fog of war over the world: everything the local player doesn't see now is darkened, on the ground
/// and up the belts and buildings alike (a full-screen pass that finds each pixel's ground spot from the
/// depth buffer). Edges are blurred a cell and fade in and out over a moment instead of snapping. Its texture also shades the
/// minimap. Placeholder look.
/// </summary>
public partial class FogOverlay : MeshInstance3D
{
    const float FadeSeconds = 0.3f;  // real time for a cell to fade in or out
    const float Darkness = 0.55f;    // how dark unseen ground gets

    const string ShaderCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, skip_vertex_transform, fog_disabled, shadows_disabled;
uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
uniform sampler2D fog : filter_linear, repeat_disable;
uniform vec4 map_rect;              // min x, min z, width, depth (m)
uniform float darkness = 0.55;
uniform vec3 shade : source_color = vec3(0.02, 0.03, 0.06);
void vertex() { POSITION = vec4(VERTEX.xy, 1.0, 1.0); }
void fragment() {
    float depth = texture(depth_texture, SCREEN_UV).x;
    vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
    view.xyz /= view.w;
    vec3 world = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;
    vec2 uv = (world.xz - map_rect.xy) / map_rect.zw;
    bool inside = uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0;
    float hidden = inside ? texture(fog, uv).a : 1.0;
    ALBEDO = shade;
    ALPHA = hidden * darkness;
}";

    Simulation _sim = null!;
    Image _image = null!;
    ImageTexture _texture = null!;
    float[] _target = [], _shown = [];
    byte[] _bytes = [];
    int _player = -1;
    bool _all;

    /// <summary>The fog as a texture over the map: black, alpha 1 where unseen. For the minimap.</summary>
    public ImageTexture Texture => _texture;

    public void Setup(Simulation sim)
    {
        _sim = sim;
        int w = Math.Max(1, sim.VisionWidth), d = Math.Max(1, sim.VisionDepth);
        (_target, _shown, _bytes) = (new float[w * d], new float[w * d], new byte[w * d * 4]);
        Array.Fill(_shown, 1);
        _image = Image.CreateFromData(w, d, false, Image.Format.Rgba8, _bytes);
        _texture = ImageTexture.CreateFromImage(_image);
        var material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        material.SetShaderParameter("fog", _texture);
        material.SetShaderParameter("map_rect", new Vector4(sim.VisionMinX, sim.VisionMinZ, w * Simulation.VisionCell, d * Simulation.VisionCell));
        material.SetShaderParameter("darkness", Darkness);
        Mesh = new QuadMesh { Size = new Vector2(2, 2) };
        MaterialOverride = material;
        CastShadow = ShadowCastingSetting.Off;
        ExtraCullMargin = 16384; // it covers the screen wherever the camera is
    }

    // Let go of the texture with the node: held on the C# side until collected, it would outlive the renderer at exit.
    public override void _ExitTree()
    {
        MaterialOverride = null;
        _texture?.Dispose();
        _image?.Dispose();
    }

    /// <summary>Follows the local player's sight; `delta` is real seconds since the last frame.</summary>
    public void Sync(float delta)
    {
        if (_sim is null) return;
        Visible = !Sight.All;
        if (Sight.All) return;
        bool jump = _player != Sight.Player || _all; // a new viewer: no fade from the old one's view
        (_player, _all) = (Sight.Player, Sight.All);
        _sim.CopyVisibility(Sight.Player, _target);
        float step = FadeSeconds > 0 ? delta / FadeSeconds : 1;
        for (int i = 0; i < _target.Length; i++) _shown[i] = jump ? _target[i] : Mathf.MoveToward(_shown[i], _target[i], step);
        // Blurred a cell each way, so the edge is a soft curve rather than the cells' steps.
        int w = _image.GetWidth(), d = _image.GetHeight();
        for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                int n = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= w || zz >= d) continue;
                        sum += _shown[zz * w + xx];
                        n++;
                    }
                _bytes[(z * w + x) * 4 + 3] = (byte)((1 - sum / n) * 255);
            }
        _image.SetData(_image.GetWidth(), _image.GetHeight(), false, Image.Format.Rgba8, _bytes);
        _texture.Update(_image);
    }
}
