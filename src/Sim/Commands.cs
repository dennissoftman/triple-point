using System.Numerics;

namespace Sim;

/// <summary>
/// An order. Player input and AI both produce these.
/// Queued (shift-click) orders go after the unit's current ones instead of replacing them.
/// </summary>
public abstract record Command;

public sealed record MoveCommand(int UnitId, Vector3 Target, bool Queued = false) : Command;

/// <summary>Breaks a segment instantly. For tests and scripted events; players break segments by attacking them.</summary>
public sealed record BreakSegmentCommand(int Line, int Segment) : Command;

/// <summary>Walk into weapon range and fire until the segment breaks.</summary>
public sealed record AttackSegmentCommand(int UnitId, int Line, int Segment, bool Queued = false) : Command;

/// <summary>Walk to the segment and restore it to full health; a broken one works again once full.</summary>
public sealed record RepairSegmentCommand(int UnitId, int Line, int Segment, bool Queued = false) : Command;

public enum SimEventKind { UnitArrived, PackageLost, SegmentBroken, SegmentRepaired }

/// <summary>
/// Something that happened during a tick, for effects, sound and UI.
/// Id is a unit or package id; for segment events it is the line index.
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id, int Segment = -1);
