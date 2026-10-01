namespace Sim;

/// <summary>
/// What two machines say to each other, as bytes: a kind, then its fields. Before a match: Hello (the
/// joiner's protocol, build and data), Accept or Reject, then Setup (the host starts it). In one: Turn
/// (a player's commands for a tick, with its state hash now and then), Pause, Desync, Dump, Bye. The
/// transport (ENet, Godot side) only moves these.
/// </summary>
public static class NetMessage
{
    /// <summary>Bumped whenever a message changes: machines on different protocols refuse each other.</summary>
    public const int Protocol = 1;

    public enum Kind : byte { Turn = 1, Pause = 2, Desync = 3, Dump = 4, Bye = 5, Hello = 10, Accept = 11, Reject = 12, Setup = 13 }

    /// <summary>Why a host turned a joiner away.</summary>
    public enum RejectReason : byte { Protocol = 1, Build = 2, Data = 3, Full = 4 }

    public readonly record struct Hello(int Protocol, string Build, ulong Data);

    /// <summary>The match: its seed, the host's side, the input delay in ticks.</summary>
    public readonly record struct Setup(uint Seed, int HostPlayer, int Delay);

    public static Kind KindOf(byte[] message) => (Kind)message[0];

    public static byte[] Write(Kind kind, Action<BinaryWriter>? body = null)
    {
        var bytes = new MemoryStream();
        using (var w = new BinaryWriter(bytes))
        {
            w.Write((byte)kind);
            body?.Invoke(w);
        }
        return bytes.ToArray();
    }

    public static BinaryReader Read(byte[] message)
    {
        var r = new BinaryReader(new MemoryStream(message));
        r.ReadByte(); // the kind
        return r;
    }

    public static byte[] WriteHello(Hello h) => Write(Kind.Hello, w => { w.Write(h.Protocol); w.Write(h.Build); w.Write(h.Data); });
    public static Hello ReadHello(byte[] m) { using var r = Read(m); return new Hello(r.ReadInt32(), r.ReadString(), r.ReadUInt64()); }

    public static byte[] WriteReject(RejectReason why) => Write(Kind.Reject, w => w.Write((byte)why));
    public static RejectReason ReadReject(byte[] m) { using var r = Read(m); return (RejectReason)r.ReadByte(); }

    public static byte[] WriteSetup(Setup s) => Write(Kind.Setup, w => { w.Write(s.Seed); w.Write(s.HostPlayer); w.Write(s.Delay); });
    public static Setup ReadSetup(byte[] m) { using var r = Read(m); return new Setup(r.ReadUInt32(), r.ReadInt32(), r.ReadInt32()); }

    public static void WriteHash(BinaryWriter w, StateHash h)
    {
        w.Write(h.Tick);
        for (int i = 0; i < StateHash.Sections.Length; i++) w.Write(h.Section(i));
    }

    public static StateHash ReadHash(BinaryReader r)
    {
        int tick = r.ReadInt32();
        var s = new ulong[StateHash.Sections.Length];
        for (int i = 0; i < s.Length; i++) s[i] = r.ReadUInt64();
        return new StateHash(tick, s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8]);
    }
}
