using Godot;
using SVector3 = System.Numerics.Vector3;

/// <summary>The one place where sim types become Godot types and back.</summary>
public static class SimConvert
{
    public static Vector3 ToGodot(SVector3 v) => new(v.X, v.Y, v.Z);
    public static SVector3 ToSim(Vector3 v) => new(v.X, v.Y, v.Z);
}
