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

/// <summary>
/// Go to one of its owner's buildings that mends its class (barracks infantry, factory vehicles) and heal
/// there to full, a squad regaining lost members, paid as it heals.
/// </summary>
public sealed record MendCommand(int Player, int UnitId, int BuildingId, bool Queued = false) : Command(Player);

/// <summary>
/// An infantry squad goes into a garrison building that's empty or already its owner's, if there's room
/// when it gets there. Any later order brings it out first.
/// </summary>
public sealed record GarrisonCommand(int Player, int UnitId, int BuildingId, bool Queued = false) : Command(Player);

/// <summary>Squads inside a garrison building of yours come out: one (UnitId), or all of them (UnitId -1).</summary>
public sealed record ExitCommand(int Player, int BuildingId, int UnitId = -1) : Command(Player);

/// <summary>A builder or engineer repairs one of its owner's damaged buildings, posts or defenses, paid as it goes.</summary>
public sealed record RepairCommand(int Player, int UnitId, int TargetId, bool Queued = false) : Command(Player);

/// <summary>Auto-retreat on: below RetreatHealth of its health, the unit goes to mend on its own.</summary>
public sealed record SetRetreatCommand(int Player, int UnitId, bool On) : Command(Player);

/// <summary>Walk to the segment and restore it to full health; a broken one works again once full.</summary>
public sealed record RepairSegmentCommand(int Player, int UnitId, int Line, int Segment, bool Queued = false) : Command(Player);
/// <summary>A builder or engineer paves a dirt road piece: tougher, and faster to move along (paid as it works).</summary>
public sealed record PaveSegmentCommand(int Player, int UnitId, int Line, int Segment, bool Queued = false) : Command(Player);

/// <summary>Drop every order, current and queued, and stand there: an idle unit (it fires at what's in range and answers fire).</summary>
public sealed record StopCommand(int Player, int UnitId) : Command(Player);

/// <summary>
/// Stop and hold this spot: fire at what's in range, but never chase, never answer fire from out of range,
/// never give way to other units and never step off a road for a truck. Any other order ends it.
/// </summary>
public sealed record HoldCommand(int Player, int UnitId) : Command(Player);


/// <summary>Adds `Count` units of `UnitType` (an id in units.json) to the back of a building's queue, if it produces that type (up to Simulation.MaxQueue in all).</summary>
public sealed record ProduceCommand(int Player, int BuildingId, string UnitType, int Count = 1) : Command(Player);

/// <summary>Removes the queue entry at `Index`; the one in production refunds what was paid for it.</summary>
public sealed record CancelProductionCommand(int Player, int BuildingId, int Index) : Command(Player);


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

/// <summary>Destroys a unit, post or building instantly. For tests and scripted events.</summary>
public sealed record DestroyCommand(int TargetId) : Command(Sim.Player.None);

public enum SimEventKind
{
    UnitArrived, UnitDied, PackageLost, PackageGathered, PickupCollected, GathererDestroyed, TruckDestroyed,
    SegmentBroken, SegmentRepaired, SegmentPaved, ShellHit,
    UnitProduced, BuildingDestroyed, BuildingPlaced, BuildingCompleted, BuildBlocked,
    GraceStarted, GraceEnded, PlayerLost, GameOver,
}

/// <summary>
/// Something that happened during a tick, for effects, sound and UI.
/// Id: a unit, truck, gatherer or building id (the new unit for UnitProduced, the builder for BuildBlocked;
/// the truck for PackageLost and PackageGathered: what it still carried at the end, or what it unloaded);
/// the player for GraceStarted, GraceEnded and PlayerLost; the winner (Player.None: a draw) for GameOver; the line index for
/// segment events; the projectile id for ShellHit (it's gone by then; views know where they last drew it).
/// Index: the segment for segment events, the line for TruckDestroyed, the gatherer id for PackageGathered, the collecting unit for PickupCollected (Id: the pickup), the building for UnitProduced, the builder
/// for BuildingPlaced, why for BuildBlocked (Simulation.BlockedByTheSite or BlockedByMoney), and for
/// BuildingCompleted what the building became: itself, or the gatherer post or defense unit that replaced it.
/// </summary>
public readonly record struct SimEvent(SimEventKind Kind, int Id, int Index = -1);
