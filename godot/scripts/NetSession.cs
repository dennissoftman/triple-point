using System;
using System.Linq;
using Godot;
using Sim;

/// <summary>
/// A two-player network session over ENet, kept across scenes (an autoload): host on a port or join an
/// address, the handshake, the lobby, then the match's messages between the peer and the match's
/// Lockstep. The joiner says Hello (protocol, build, data); the host accepts only the same three, and
/// refuses a second joiner. The host starts the match (Setup: seed, its side, the input delay) and both
/// load the map. Messages go on ENet's reliable, ordered channel. Placeholder for anything fancier (a relay,
/// reconnecting): direct IP only.
/// </summary>
public partial class NetSession : Node
{
    public const int DefaultPort = 7777;
    public const int DefaultDelay = 3; // ticks: 150 ms, for round trips up to about 250 ms
    const double LingerSeconds = 5;
    const int MaxDelay = 20; // ticks: a second; a host asking for more is broken or up to something

    public enum Status { Idle, Hosting, Joining, Ready, InMatch, Failed }

    public static NetSession Instance { get; private set; } = null!;

    public Status State { get; private set; } = Status.Idle;
    /// <summary>Why it failed or ended, as a locale key with its argument (Status Failed, or a dropped lobby).</summary>
    public (string Key, string Arg) Problem { get; private set; } = ("", "");
    public bool IsHost { get; private set; }
    /// <summary>The match's lockstep, from Setup until the session closes.</summary>
    public Lockstep? Match { get; private set; }

    ENetMultiplayerPeer? _peer;
    int _other = -1;          // the peer's id, once connected
    double _closeIn = -1;     // s until a session that's leaving closes for good

    public override void _EnterTree() => Instance = this;

    public override void _Ready() => ProcessMode = ProcessModeEnum.Always;

    /// <summary>This machine's IPv4 addresses a friend could join (LAN, VPN), not loopback or link-local.</summary>
    public static string[] LocalAddresses() =>
        [.. IP.GetLocalAddresses().Where(a => a.Count(c => c == '.') == 3 && !a.StartsWith("127.") && !a.StartsWith("169.254."))];

    public bool Host(int port)
    {
        Close();
        _peer = new ENetMultiplayerPeer();
        if (_peer.CreateServer(port, 1) != Error.Ok) return Fail("net.fail.port", port.ToString());
        Hook();
        (IsHost, State) = (true, Status.Hosting);
        return true;
    }

    public bool Join(string address, int port)
    {
        Close();
        _peer = new ENetMultiplayerPeer();
        if (_peer.CreateClient(address, port) != Error.Ok) return Fail("net.fail.address", address);
        Hook();
        (IsHost, State) = (false, Status.Joining);
        return true;
    }

    void Hook()
    {
        _peer!.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
        _peer.PeerConnected += id =>
        {
            _other = (int)id;
            if (!IsHost) Send(NetMessage.WriteHello(new NetMessage.Hello(NetMessage.Protocol, GameFiles.Build(), GameFiles.Hash())));
        };
        _peer.PeerDisconnected += id =>
        {
            if (id != _other) return;
            _other = -1;
            if (Match is not null) Match.PeerGone();
            else if (IsHost && State == Status.Ready) State = Status.Hosting; // back to waiting for someone
            else if (!IsHost) Fail("net.fail.lost", "");
        };
    }

    /// <summary>The host starts the match: its side, a fresh seed, the delay. Both machines load the map.</summary>
    public void StartMatch(int hostPlayer, int delay = DefaultDelay)
    {
        if (!IsHost || State != Status.Ready) return;
        var setup = new NetMessage.Setup((uint)GD.Randi() | 1, hostPlayer, delay);
        Send(NetMessage.WriteSetup(setup));
        Begin(setup, hostPlayer);
    }

    void Begin(NetMessage.Setup setup, int localPlayer)
    {
        Match = new Lockstep(localPlayer, 2, setup.Delay, setup.Seed, MatchSetup.MatchScene);
        State = Status.InMatch;
        MatchSetup.Current = new MatchSetup.Choice(localPlayer, false, Sim.Ai.AiLevel.Normal, setup.Seed, Networked: true);
        GetTree().ChangeSceneToFile(MatchSetup.MatchScene);
    }

    /// <summary>The round trip to the peer, ms (0 when not connected).</summary>
    public int Ping => _peer is not null && _other >= 0 && _peer.GetPeer(_other) is { } p ? (int)p.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) : 0;

    /// <summary>
    /// Ends the session: tells the peer (in a match), lets ENet say goodbye, then closes a few seconds later:
    /// what's still on its way (a desync report, a megabyte or so) gets there meanwhile.
    /// </summary>
    public void Leave()
    {
        if (_peer is null) return;
        Match?.Leave();
        Flush();
        if (_other >= 0) _peer.GetPeer(_other)?.PeerDisconnectLater();
        (Match, State, _closeIn) = (null, Status.Idle, LingerSeconds);
    }

    void Close()
    {
        _peer?.Close();
        (_peer, _other, Match, _closeIn) = (null, -1, null, -1);
        if (State != Status.Failed) State = Status.Idle;
    }

    /// <summary>Back to idle after a failure has been shown.</summary>
    public void Reset() { Close(); (State, Problem) = (Status.Idle, ("", "")); }

    bool Fail(string key, string arg)
    {
        _peer?.Close();
        (_peer, _other, Match) = (null, -1, null);
        (State, Problem) = (Status.Failed, (key, arg));
        return false;
    }

    public override void _Process(double delta)
    {
        if (_peer is null) return;
        if (_closeIn >= 0 && (_closeIn -= delta) < 0) { Close(); return; }
        _peer.Poll();
        if (_peer is null) return; // a handler closed it
        if (State == Status.Joining && _peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected)
        {
            Fail("net.fail.connect", "");
            return;
        }
        while (_peer is not null && _peer.GetAvailablePacketCount() > 0)
        {
            int from = _peer.GetPacketPeer();
            var message = _peer.GetPacket();
            if (from == _other && message.Length > 0) Handle(message);
        }
    }

    // Once a match is on, everything goes to its Lockstep, which checks it (a lobby message then is a
    // violation). Before, the lobby's own messages; one that doesn't parse drops whoever sent it.
    void Handle(byte[] message)
    {
        if (Match is not null) { Match.Receive(message); return; }
        try { HandleLobby(message); }
        catch (Exception e) when (e is System.IO.EndOfStreamException or System.IO.InvalidDataException or System.IO.IOException or ArgumentException or OverflowException)
        {
            GD.Print($"A malformed lobby message ({e.GetType().Name}); dropping the other side");
            if (IsHost) { _peer?.GetPeer(_other)?.PeerDisconnectLater(); State = Status.Hosting; }
            else Fail("net.fail.lost", "");
        }
    }

    void HandleLobby(byte[] message)
    {
        switch (NetMessage.KindOf(message))
        {
            case NetMessage.Kind.Hello when IsHost:
                var hello = NetMessage.ReadHello(message);
                NetMessage.RejectReason? why = hello.Protocol != NetMessage.Protocol ? NetMessage.RejectReason.Protocol
                    : hello.Build != GameFiles.Build() ? NetMessage.RejectReason.Build
                    : hello.Data != GameFiles.Hash() ? NetMessage.RejectReason.Data
                    : State != Status.Hosting ? NetMessage.RejectReason.Full : null;
                if (why is { } reason)
                {
                    Send(NetMessage.WriteReject(reason));
                    GD.Print($"Refused a player: {reason}");
                    _peer!.GetPeer(_other)?.PeerDisconnectLater();
                    return;
                }
                Send(NetMessage.Write(NetMessage.Kind.Accept));
                State = Status.Ready;
                break;
            case NetMessage.Kind.Accept when !IsHost: State = Status.Ready; break;
            case NetMessage.Kind.Reject when !IsHost:
                Fail(NetMessage.ReadReject(message) switch
                {
                    NetMessage.RejectReason.Protocol => "net.reject.protocol",
                    NetMessage.RejectReason.Build => "net.reject.build",
                    NetMessage.RejectReason.Data => "net.reject.data",
                    _ => "net.reject.full",
                }, "");
                break;
            case NetMessage.Kind.Setup when !IsHost && State == Status.Ready:
                var setup = NetMessage.ReadSetup(message);
                if (setup.HostPlayer is not (0 or 1) || setup.Delay is < 1 or > MaxDelay) { Fail("net.fail.lost", ""); return; }
                Begin(setup, 1 - setup.HostPlayer);
                break;
        }
    }

    void Send(byte[] message)
    {
        if (_peer is null || _other < 0) return;
        _peer.SetTargetPeer(_other);
        _peer.PutPacket(message);
    }

    /// <summary>Sends what the match has queued. SimHost calls it after its ticks, so turns leave the same frame.</summary>
    public void Flush()
    {
        if (Match is not null)
            while (Match.Outbox.Count > 0) Send(Match.Outbox.Dequeue());
        _peer?.Host?.Flush(); // onto the wire now, not at the next poll (a machine quitting would drop its last turns)
    }
}
