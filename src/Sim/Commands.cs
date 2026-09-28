using System.Numerics;

namespace Sim;

/// <summary>An order. Player input and AI both produce these.</summary>
public abstract record Command;

public sealed record MoveCommand(int UnitId, Vector3 Target) : Command;

/// <summary>Stand-in for combat damage until there is combat.</summary>
public sealed record BreakSegmentCommand(int Line, int Segment) : Command;

public sealed record RepairSegmentCommand(int UnitId, int Line, int Segment) : Command;

public enum SimEventKind { UnitArrived, PackageLost, SegmentBroken, SegmentRepaired }

/// <summary>
/// Something that happened during a tick, for effects, sound and UI.
/// Id is a unit or package id; for segment events it is the line index.
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id, int Segment = -1);
