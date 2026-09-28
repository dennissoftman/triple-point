using System.Numerics;

namespace Sim;

/// <summary>An order. Player input and AI both produce these.</summary>
public abstract record Command;

public sealed record MoveCommand(int UnitId, Vector3 Target) : Command;

public enum SimEventKind { UnitArrived, PackageLost }

/// <summary>Something that happened during a tick, for effects, sound and UI.</summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id);
