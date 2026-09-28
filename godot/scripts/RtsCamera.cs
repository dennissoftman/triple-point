using System;
using Godot;

/// <summary>
/// RTS camera: a focus point on the ground plus a distance, at a fixed pitch and no rotation.
/// Pans with the camera_* actions, the screen edges and a middle-mouse grab; zooms with the wheel.
/// Runs on real time, so game speed doesn't change how it moves.
/// </summary>
public partial class RtsCamera : Camera3D
{
    const float ReferenceDistance = 40f; // PanSpeed applies at this zoom; closer is slower, farther faster

    [Export] public float PitchDegrees = 55f;
    [Export] public Vector2 Focus = new(6, -0.5f); // ground x, z under the screen center
    [Export] public float Distance = 42f;          // m from the focus point
    [Export] public float MinDistance = 15f, MaxDistance = 60f;
    [Export] public float ZoomStep = 0.15f;        // share of the distance per wheel notch
    [Export] public float ZoomSharpness = 12f;     // how fast zoom catches up (1/s)
    [Export] public float PanSpeed = 25f;          // m/s at ReferenceDistance
    [Export] public bool EdgeScroll = true;
    [Export] public float EdgeMargin = 8f;         // px
    [Export] public Rect2 Bounds = new(-40, -30, 80, 60); // where the focus may go (x, z)

    float _targetDistance;
    Vector3? _grab; // ground point held under the cursor during a middle-mouse drag
    // Only true once the mouse has moved over the window: the last known position of a mouse that was
    // never inside (or has left) would otherwise scroll the map forever.
    bool _mouseInside;

    public override void _Ready()
    {
        _targetDistance = Distance;
        Apply();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit) _mouseInside = false;
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion) _mouseInside = true; // motion only arrives while the cursor is over the window
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("camera_zoom_in")) Zoom(1 - ZoomStep);
        else if (e.IsActionPressed("camera_zoom_out")) Zoom(1 / (1 - ZoomStep));
        else if (e.IsActionPressed("camera_grab") && e is InputEventMouse down) _grab = GroundPoint(down.Position);
        else if (e.IsActionReleased("camera_grab")) _grab = null;
        else if (e is InputEventMouseMotion motion && _grab is Vector3 grab && GroundPoint(motion.Position) is Vector3 now)
        {
            Focus += new Vector2(grab.X - now.X, grab.Z - now.Z);
            ClampFocus();
            Apply();
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var pan = Input.GetVector("camera_left", "camera_right", "camera_forward", "camera_back");
        if (EdgeScroll && _grab is null) pan += EdgePan();
        if (pan.LengthSquared() > 1) pan = pan.Normalized();

        Focus += pan * PanSpeed * (Distance / ReferenceDistance) * dt;
        ClampFocus();
        Distance = Mathf.Lerp(Distance, _targetDistance, 1 - MathF.Exp(-ZoomSharpness * dt));
        Apply();
    }

    /// <summary>Where a screen point hits the ground plane (y = 0), if it does.</summary>
    public Vector3? GroundPoint(Vector2 screen) =>
        new Plane(Vector3.Up, 0).IntersectsRay(ProjectRayOrigin(screen), ProjectRayNormal(screen));

    void Zoom(float factor) => _targetDistance = Math.Clamp(_targetDistance * factor, MinDistance, MaxDistance);

    Vector2 EdgePan()
    {
        if (!_mouseInside || !DisplayServer.WindowIsFocused()) return Vector2.Zero;
        var mouse = GetViewport().GetMousePosition();
        var size = GetViewport().GetVisibleRect().Size;
        float x = mouse.X <= EdgeMargin ? -1 : mouse.X >= size.X - EdgeMargin ? 1 : 0;
        float y = mouse.Y <= EdgeMargin ? -1 : mouse.Y >= size.Y - EdgeMargin ? 1 : 0;
        return new Vector2(x, y);
    }

    void ClampFocus() =>
        Focus = new Vector2(Math.Clamp(Focus.X, Bounds.Position.X, Bounds.End.X), Math.Clamp(Focus.Y, Bounds.Position.Y, Bounds.End.Y));

    // Looks down at the focus point from `Distance` away, tilted by the pitch.
    void Apply()
    {
        float pitch = Mathf.DegToRad(PitchDegrees);
        var back = new Vector3(0, MathF.Sin(pitch), MathF.Cos(pitch));
        Transform = new Transform3D(Basis.FromEuler(new Vector3(-pitch, 0, 0)), new Vector3(Focus.X, 0, Focus.Y) + back * Distance);
    }
}
