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

/// <summary>Head for a point, stopping to fight anything that comes into range on the way (weakest first).</summary>
public sealed record AttackMoveCommand(int Player, int UnitId, Vector3 Target, bool Queued = false) : Command(Player);

/// <summary>Walk into weapon range of a belt segment and fire until it breaks.</summary>
public sealed record AttackSegmentCommand(int Player, int UnitId, int Line, int Segment, bool Queued = false) : Command(Player);

/// <summary>Walk to the segment and restore it to full health; a broken one works again once full.</summary>
public sealed record RepairSegmentCommand(int Player, int UnitId, int Line, int Segment, bool Queued = false) : Command(Player);

/// <summary>Set a switch's live output (an index into its Outputs). Only its owner can, from anywhere.</summary>
public sealed record SetJunctionCommand(int Player, int Junction, int Output) : Command(Player);

/// <summary>Adds a unit of `UnitType` (an id in units.json) to the back of a building's queue, if it produces that type and has room.</summary>
public sealed record ProduceCommand(int Player, int BuildingId, string UnitType) : Command(Player);

/// <summary>Removes the queue entry at `Index`; the one in production refunds what was paid for it.</summary>
public sealed record CancelProductionCommand(int Player, int BuildingId, int Index) : Command(Player);

/// <summary>Repeat on: each finished unit's type goes back to the end of the queue.</summary>
public sealed record SetRepeatCommand(int Player, int BuildingId, bool Repeat) : Command(Player);

/// <summary>Where a building's finished units go.</summary>
public sealed record SetRallyCommand(int Player, int BuildingId, Vector3 Rally) : Command(Player);

/// <summary>
/// Walk to `Position` and put up a building of `BuildingType` (an id in buildings.json) there, its exit
/// facing `Heading` (radians). Builders only, and only types they can build. The foundation is laid on
/// arrival, if the spot is still clear, and grows while a builder works on it.
/// </summary>
public sealed record BuildCommand(int Player, int UnitId, string BuildingType, Vector3 Position, float Heading = 0, bool Queued = false) : Command(Player);

/// <summary>Walk to one of your unfinished buildings and work on it. Builders only.</summary>
public sealed record ResumeBuildCommand(int Player, int UnitId, int BuildingId, bool Queued = false) : Command(Player);

/// <summary>Breaks a segment instantly. For tests and scripted events; players break segments by attacking them.</summary>
public sealed record BreakSegmentCommand(int Line, int Segment) : Command(Sim.Player.None);

public enum SimEventKind
{
    UnitArrived, UnitDied, PackageLost, PackageGathered, GathererDestroyed,
    SegmentBroken, SegmentRepaired, JunctionCaptured, JunctionSwitched, ShellHit,
    UnitProduced, BuildingDestroyed, BuildingPlaced, BuildingCompleted, BuildBlocked,
}

/// <summary>
/// Something that happened during a tick, for effects, sound and UI.
/// Id: a unit, package, gatherer or building id (the new unit for UnitProduced, the builder for BuildBlocked); the line index for segment events; the junction index for junction
/// events; the projectile id for ShellHit (it's gone by then; views know where they last drew it).
/// Index: the segment for segment events, the gatherer id for PackageGathered, the new owner for
/// JunctionCaptured, the output for JunctionSwitched, the building for UnitProduced, the builder for BuildingPlaced, and for
/// BuildingCompleted what the building became: itself, or the gatherer post or defense unit that replaced it.
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id, int Index = -1);
