// Contains all the network messages that we need.
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Networking;

// client to server ////////////////////////////////////////////////////////////
public partial class LoginMsg : MessageBase {
    public static short MsgId = 1000;
    public string player;
    public string version;
}

public partial class ChangeTeamMsg : MessageBase {
    public static short MsgId = 1001;
    public Team team;
}

public partial class ChangeHeroMsg : MessageBase {
    public static short MsgId = 1002;
    public int heroIndex;
}

// 'SetReady' message would be too confusing since UNET uses one too
public partial class LockMsg : MessageBase {
    public static short MsgId = 1003;
}

// server to client ////////////////////////////////////////////////////////////
// we need an error msg packet because we can't use TargetRpc with the Network-
// Manager, since it's not a MonoBehaviour.
public partial class ErrorMsg : MessageBase {
    public static short MsgId = 2000;
    public string text;
    public bool causesDisconnect;
}

public partial class LobbyUpdateMsg : MessageBase {
    public static short MsgId = 2001;
    public LobbyPlayer[] players;
}