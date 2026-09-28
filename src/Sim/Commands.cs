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

/// <summary>Walk to the junction and set which output is live (an index into its Outputs).</summary>
public sealed record SwitchJunctionCommand(int UnitId, int Junction, int Output, bool Queued = false) : Command;

public enum SimEventKind { UnitArrived, PackageLost, PackageGathered, SegmentBroken, SegmentRepaired, JunctionSwitched }

/// <summary>
/// Something that happened during a tick, for effects, sound and UI.
/// Id is a unit or package id, or a line index for segment events, or a junction index for JunctionSwitched.
/// Index is the segment for segment events, the output for JunctionSwitched, the gatherer id for PackageGathered.
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id, int Index = -1);
