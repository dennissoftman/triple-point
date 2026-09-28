using System.Numerics;

namespace Sim;

/// <summary>
/// An order, issued by a player. Player input and AI both produce these. The sim ignores unit orders
/// from a player who doesn't own the unit. Queued (shift-click) orders go after the unit's current ones
/// instead of replacing them.
/// </summary>
public abstract record Command(int Player);

public sealed record MoveCommand(int Player, int UnitId, Vector3 Target, bool Queued = false) : Command(Player);

/// <summary>Chase an enemy unit or gatherer post into range and fire until it's destroyed.</summary>
public sealed record AttackCommand(int Player, int UnitId, int TargetId, bool Queued = false) : Command(Player);

/// <summary>Walk into weapon range of a belt segment and fire until it breaks.</summary>
public sealed record AttackSegmentCommand(int Player, int UnitId, int Line, int Segment, bool Queued = false) : Command(Player);

/// <summary>Walk to the segment and restore it to full health; a broken one works again once full.</summary>
public sealed record RepairSegmentCommand(int Player, int UnitId, int Line, int Segment, bool Queued = false) : Command(Player);

/// <summary>Set a switch's live output (an index into its Outputs). Only its owner can, from anywhere.</summary>
public sealed record SetJunctionCommand(int Player, int Junction, int Output) : Command(Player);

/// <summary>Breaks a segment instantly. For tests and scripted events; players break segments by attacking them.</summary>
public sealed record BreakSegmentCommand(int Line, int Segment) : Command(Sim.Player.None);

public enum SimEventKind
{
    UnitArrived, UnitDied, PackageLost, PackageGathered, GathererDestroyed,
    SegmentBroken, SegmentRepaired, JunctionCaptured, JunctionSwitched,
}

/// <summary>
/// Something that happened during a tick, for effects, sound and UI.
/// Id: a unit, package or gatherer id; the line index for segment events; the junction index for junction events.
/// Index: the segment for segment events, the gatherer id for PackageGathered, the new owner for
/// JunctionCaptured, the output for JunctionSwitched.
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id, int Index = -1);
