using System.Numerics;

namespace Sim;

/// <summary>
/// Commands as bytes, for the network and replays: a tag per command type, then its fields in order.
/// Floats go as their IEEE bits, so a command arrives exactly as it was issued. The tags are the format:
/// never renumber one, only add. CommandCodecTests round-trips every Command type there is, so a new one
/// can't be forgotten here.
/// </summary>
public static class CommandCodec
{
    public static void Write(BinaryWriter w, Command command)
    {
        switch (command)
        {
            case MoveCommand c: Tag(w, 1, c.Player); w.Write(c.UnitId); Write(w, c.Target); w.Write(c.Queued); break;
            case AttackCommand c: Tag(w, 2, c.Player); w.Write(c.UnitId); w.Write(c.TargetId); w.Write(c.Queued); break;
            case AttackMoveCommand c: Tag(w, 3, c.Player); w.Write(c.UnitId); Write(w, c.Target); w.Write(c.Queued); break;
            case AttackSegmentCommand c: Tag(w, 4, c.Player); w.Write(c.UnitId); w.Write(c.Line); w.Write(c.Segment); w.Write(c.Queued); break;
            case MendCommand c: Tag(w, 5, c.Player); w.Write(c.UnitId); w.Write(c.BuildingId); w.Write(c.Queued); break;
            case GarrisonCommand c: Tag(w, 6, c.Player); w.Write(c.UnitId); w.Write(c.BuildingId); w.Write(c.Queued); break;
            case ExitCommand c: Tag(w, 7, c.Player); w.Write(c.BuildingId); w.Write(c.UnitId); break;
            case RepairCommand c: Tag(w, 8, c.Player); w.Write(c.UnitId); w.Write(c.TargetId); w.Write(c.Queued); break;
            case SetRetreatCommand c: Tag(w, 9, c.Player); w.Write(c.UnitId); w.Write(c.On); break;
            case RepairSegmentCommand c: Tag(w, 10, c.Player); w.Write(c.UnitId); w.Write(c.Line); w.Write(c.Segment); w.Write(c.Queued); break;
            case PaveSegmentCommand c: Tag(w, 11, c.Player); w.Write(c.UnitId); w.Write(c.Line); w.Write(c.Segment); w.Write(c.Queued); break;
            case StopCommand c: Tag(w, 12, c.Player); w.Write(c.UnitId); break;
            case HoldCommand c: Tag(w, 13, c.Player); w.Write(c.UnitId); break;
            case ProduceCommand c: Tag(w, 14, c.Player); w.Write(c.BuildingId); w.Write(c.UnitType); w.Write(c.Count); break;
            case CancelProductionCommand c: Tag(w, 15, c.Player); w.Write(c.BuildingId); w.Write(c.Index); break;
            case SetRallyCommand c: Tag(w, 16, c.Player); w.Write(c.BuildingId); Write(w, c.Rally); break;
            case BuildCommand c: Tag(w, 17, c.Player); w.Write(c.UnitId); w.Write(c.BuildingType); Write(w, c.Position); w.Write(c.Heading); w.Write(c.Queued); break;
            case ResumeBuildCommand c: Tag(w, 18, c.Player); w.Write(c.UnitId); w.Write(c.BuildingId); w.Write(c.Queued); break;
            case BreakSegmentCommand c: Tag(w, 19, c.Player); w.Write(c.Line); w.Write(c.Segment); break;
            case DestroyCommand c: Tag(w, 20, c.Player); w.Write(c.TargetId); break;
            default: throw new ArgumentException($"CommandCodec has no tag for {command.GetType().Name}.");
        }
    }

    public static Command Read(BinaryReader r)
    {
        int tag = r.ReadByte(), player = r.ReadInt32();
        return tag switch
        {
            1 => new MoveCommand(player, r.ReadInt32(), ReadVector(r), r.ReadBoolean()),
            2 => new AttackCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            3 => new AttackMoveCommand(player, r.ReadInt32(), ReadVector(r), r.ReadBoolean()),
            4 => new AttackSegmentCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            5 => new MendCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            6 => new GarrisonCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            7 => new ExitCommand(player, r.ReadInt32(), r.ReadInt32()),
            8 => new RepairCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            9 => new SetRetreatCommand(player, r.ReadInt32(), r.ReadBoolean()),
            10 => new RepairSegmentCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            11 => new PaveSegmentCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            12 => new StopCommand(player, r.ReadInt32()),
            13 => new HoldCommand(player, r.ReadInt32()),
            14 => new ProduceCommand(player, r.ReadInt32(), r.ReadString(), r.ReadInt32()),
            15 => new CancelProductionCommand(player, r.ReadInt32(), r.ReadInt32()),
            16 => new SetRallyCommand(player, r.ReadInt32(), ReadVector(r)),
            17 => new BuildCommand(player, r.ReadInt32(), r.ReadString(), ReadVector(r), r.ReadSingle(), r.ReadBoolean()),
            18 => new ResumeBuildCommand(player, r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()),
            19 => Scripted(player, new BreakSegmentCommand(r.ReadInt32(), r.ReadInt32())),
            20 => Scripted(player, new DestroyCommand(r.ReadInt32())),
            _ => throw new InvalidDataException($"Unknown command tag {tag}."),
        };
    }

    // Scripted commands are always Player.None's; anything else in the bytes is corrupt.
    static Command Scripted(int player, Command command) =>
        player == command.Player ? command : throw new InvalidDataException($"A scripted command from player {player}.");

    static void Tag(BinaryWriter w, byte tag, int player) { w.Write(tag); w.Write(player); }

    static void Write(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
