using System;
using Godot;

/// <summary>Where the camera looks and from how far: all it takes to restore a viewpoint, or later to replay one.</summary>
public readonly record struct CameraView(Vector2 Focus, float Distance);

/// <summary>
/// RTS camera: a focus point on the ground plus a distance, at a fixed pitch and no rotation.
/// Pans with the camera_* actions, the screen edges and a middle-mouse grab; zooms with the wheel;
/// FlyTo glides to a saved view. Runs on real time, so game speed doesn't change how it moves.
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
    [Export] public float FlightSeconds = 0.6f;    // how long FlyTo takes unless told otherwise
    [Export] public bool EdgeScroll = true;
    [Export] public float EdgeMargin = 8f;         // px
    [Export] public Rect2 Bounds = new(-40, -30, 80, 60); // where the focus may go (x, z)

    readonly record struct Flight(CameraView From, CameraView To, float Elapsed, float Duration);

    float _targetDistance;
    Flight? _flight;
    Vector3? _grab; // ground point held under the cursor during a middle-mouse drag
    // Only true once the mouse has moved over the window: the last known position of a mouse that was
    // never inside (or has left) would otherwise scroll the map forever.
    bool _mouseInside;

    /// <summary>The starting zoom, as authored; a sensible zoom for views that don't have one yet.</summary>
    public float HomeDistance { get; private set; }

    /// <summary>The current view. Uses the zoom being eased toward, so a view saved mid-zoom lands where the zoom was going.</summary>
    public CameraView View => new(Focus, _targetDistance);

    public override void _Ready()
    {
        HomeDistance = _targetDistance = Distance;
        Apply();
    }

    /// <summary>Glides to a view (clamped to the map and zoom range) over `seconds`, easing in and out; 0 jumps.</summary>
    public void FlyTo(CameraView view, float? seconds = null)
    {
        var to = new CameraView(Clamped(view.Focus), Math.Clamp(view.Distance, MinDistance, MaxDistance));
        float duration = seconds ?? FlightSeconds;
        if (duration <= 0)
        {
            (Focus, Distance, _targetDistance, _flight) = (to.Focus, to.Distance, to.Distance, null);
            Apply();
            return;
        }
        _flight = new Flight(new CameraView(Focus, Distance), to, 0, duration);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMMouseExit) _mouseInside = false;
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion) _mouseInside = true; // motion only arrives while the cursor is over the window
    }

    // Zooming or grabbing takes over from a flight in progress.
    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("camera_zoom_in")) Zoom(1 - ZoomStep);
        else if (e.IsActionPressed("camera_zoom_out")) Zoom(1 / (1 - ZoomStep));
        else if (e.IsActionPressed("camera_grab") && e is InputEventMouse down)
        {
            _flight = null;
            _grab = GroundPoint(down.Position);
        }
        else if (e.IsActionReleased("camera_grab")) _grab = null;
        else if (e is InputEventMouseMotion motion && _grab is Vector3 grab && GroundPoint(motion.Position) is Vector3 now)
        {
            Focus = Clamped(Focus + new Vector2(grab.X - now.X, grab.Z - now.Z));
            Apply();
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var keys = Input.GetVector("camera_left", "camera_right", "camera_forward", "camera_back");
        if (_flight is Flight flight)
        {
            // Panning with the keys takes over; edge scrolling doesn't, so a resting mouse can't cut a flight short.
            if (keys == Vector2.Zero)
            {
                Fly(flight, dt);
                return;
            }
            _flight = null;
        }

        var pan = keys;
        if (EdgeScroll && _grab is null) pan += EdgePan();
        if (pan.LengthSquared() > 1) pan = pan.Normalized();

        Focus = Clamped(Focus + pan * PanSpeed * (Distance / ReferenceDistance) * dt);
        Distance = Mathf.Lerp(Distance, _targetDistance, 1 - MathF.Exp(-ZoomSharpness * dt));
        Apply();
    }

    /// <summary>Where a screen point hits the ground plane (y = 0), if it does.</summary>
    public Vector3? GroundPoint(Vector2 screen) => PointAt(screen, 0);

    /// <summary>Where the ray under a screen point meets the horizontal plane at `height`.</summary>
    public Vector3? PointAt(Vector2 screen, float height) =>
        new Plane(Vector3.Up, height).IntersectsRay(ProjectRayOrigin(screen), ProjectRayNormal(screen));

    void Fly(Flight flight, float dt)
    {
        float elapsed = flight.Elapsed + dt;
        float t = Math.Min(1, elapsed / flight.Duration);
        float s = t * t * (3 - 2 * t); // smoothstep: eases in and out
        Focus = flight.From.Focus.Lerp(flight.To.Focus, s);
        Distance = _targetDistance = Mathf.Lerp(flight.From.Distance, flight.To.Distance, s);
        _flight = t < 1 ? flight with { Elapsed = elapsed } : null;
        Apply();
    }

    void Zoom(float factor)
    {
        _flight = null;
        _targetDistance = Math.Clamp(_targetDistance * factor, MinDistance, MaxDistance);
    }

    Vector2 EdgePan()
    {
        if (!_mouseInside || !DisplayServer.WindowIsFocused()) return Vector2.Zero;
        var mouse = GetViewport().GetMousePosition();
        var size = GetViewport().GetVisibleRect().Size;
        float x = mouse.X <= EdgeMargin ? -1 : mouse.X >= size.X - EdgeMargin ? 1 : 0;
        float y = mouse.Y <= EdgeMargin ? -1 : mouse.Y >= size.Y - EdgeMargin ? 1 : 0;
        return new Vector2(x, y);
    }

    Vector2 Clamped(Vector2 focus) =>
        new(Math.Clamp(focus.X, Bounds.Position.X, Bounds.End.X), Math.Clamp(focus.Y, Bounds.Position.Y, Bounds.End.Y));

    // Looks down at the focus point from `Distance` away, tilted by the pitch.
    void Apply()
    {
        float pitch = Mathf.DegToRad(PitchDegrees);
        var back = new Vector3(0, MathF.Sin(pitch), MathF.Cos(pitch));
        Transform = new Transform3D(Basis.FromEuler(new Vector3(-pitch, 0, 0)), new Vector3(Focus.X, 0, Focus.Y) + back * Distance);
    }
}
