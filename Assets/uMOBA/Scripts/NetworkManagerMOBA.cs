// We use a custom NetworkManager that takes care of the lobby too.
//
// This is a better solution than using Unity's Network Lobby asset because:
// - The Network Lobby asset was buggy
// - It relied on HLAPI's NetworkLobby class, which is buggy
// - It had no hero selection and no binary team selection
// - It had no rejoin option after disconnects
// - Debugging was difficult because of the scene change
//
// We don't use the playerPrefab, instead all available player classes should be
// dragged into the spawnable objects property.
//
// Network Configuration modified parameters:
// - Max Buffered Packets: 16 => 512. allows to send more data to one connection
//   without running into errors too soon
// - Channels:
//   0: Reliable Fragmented for important messages that have to be delivered
//   1: Unreliable Fragmented for less important messages
//   (https://www.youtube.com/watch?v=-_0TtPY5LCc)
// - Min Update Timeout: 10 => 1. utilize the other thread as much as possible;
//   also avoids errors at the 200KB/s mark
// - Connect Timeout: 2000 => 5000. lags happen
// - Disconnect Timeout: 2000 => 5000. lags happen
// - Ping Timeout: 500 => 3000. lags happen
// - Reactor Max Recv/Send Messages: 1024 => 4096 to avoid errors.
//
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

[SerializeField]
public struct LobbyPlayer {
    public string name;
    public Team team;
    public int heroIndex;
    public bool locked;

    public LobbyPlayer(string name) {
        this.name = name;
        team = Team.Neutral;
        heroIndex = 0; // first hero selected by default
        locked = false;
    }
}

public class NetworkManagerMOBA : NetworkManager {
    [Header("Components")]
    public UILogin uiLogin;
    public UITeamSelection uiTeamSelection;
    public UIPopup uiPopup;
    public UIStatus uiStatus;

    [Header("Scenes")]
    public string playScene = "";

    // login info for the local player
    // we don't just name it 'account' to avoid collisions in handshake
    [Header("Login")]
    public string loginName = "";
    public int loginNameMaxLength = 16;

    // <conn, account> dict for the lobby
    // (people that are still selecting characters)
    Dictionary<NetworkConnection, LobbyPlayer> lobby = new Dictionary<NetworkConnection, LobbyPlayer>();

    void Start() {
        // headless mode? then automatically start a dedicated server
        // (because we can't click the button in headless mode)
        // -> only if not started yet so that addons can also start it if needed
        //    (e.g. network zones)
        if (Utils.IsHeadless() && !NetworkServer.active) {
            print("headless mode detected, starting dedicated server");
            StartServer();
        }
    }

    // client popup messages ///////////////////////////////////////////////////
    void ClientSendPopup(NetworkConnection conn, string error, bool disconnect) {
        var message = new ErrorMsg{text=error, causesDisconnect=disconnect};
        conn.Send(ErrorMsg.MsgId, message);
    }

    void OnClientReceivePopup(NetworkMessage netMsg) {
        var message = netMsg.ReadMessage<ErrorMsg>();
        print("OnClientReceivePopup: " + message.text);

        // show a popup
        uiPopup.Show(message.text);

        // disconnect if it was an important network error
        // (this is needed because the login failure message doesn't disconnect
        //  the client immediately (only after timeout))
        if (message.causesDisconnect) {
            netMsg.conn.Disconnect();

            // also stop the host if running as host
            // (host shouldn't start server but disconnect client for invalid
            //  login, which would be pointless)
            if (NetworkServer.active) StopHost();
        }
    }

    // start & stop ////////////////////////////////////////////////////////////
    public override void OnStartServer() {
        uiLogin.Hide();

        // handshake packet handlers (in OnStartServer so that reconnecting works)
        NetworkServer.RegisterHandler(LoginMsg.MsgId, OnServerLogin);
        NetworkServer.RegisterHandler(ChangeTeamMsg.MsgId, OnServerChangeTeam);
        NetworkServer.RegisterHandler(ChangeHeroMsg.MsgId, OnServerChangeHero);
        NetworkServer.RegisterHandler(LockMsg.MsgId, OnServerLock);

#if !UNITY_EDITOR
        // server only? not host mode?
        if (!NetworkClient.active) {
            // set a fixed tick rate instead of updating as often as possible
            // -> updating more than 50x/s is just a waste of CPU power that can
            //    be used by other threads like network transport instead
            // -> note: doesn't work in the editor
            Application.targetFrameRate = Mathf.RoundToInt(1f / Time.fixedDeltaTime);
            print("server tick rate set to: " + Application.targetFrameRate + " (1 / Edit->Project Settings->Time->Fixed Time Step)");
        }
#endif

        // call base function to guarantee proper functionality
        base.OnStartServer();
    }

    public override void OnStopServer() {
        print("OnStopServer");

        // call base function to guarantee proper functionality
        base.OnStopServer();
    }

    // handshake: login ////////////////////////////////////////////////////////
    public bool IsConnecting() {
        return NetworkClient.active && !ClientScene.ready;
    }

    public override void OnClientConnect(NetworkConnection conn) {
        print("OnClientConnect");

        // setup handlers
        client.RegisterHandler(ErrorMsg.MsgId, OnClientReceivePopup);
        client.RegisterHandler(LobbyUpdateMsg.MsgId, OnClientLobbyUpdate);

        // send login packet
        //
        // Application.version can be modified under:
        // Edit -> Project Settings -> Player -> Bundle Version
        var message = new LoginMsg{player=loginName, version=Application.version};
        conn.Send(LoginMsg.MsgId, message);
        print("login message was sent");

        // call base function to make sure that client becomes "ready"
        //base.OnClientConnect(conn);
        ClientScene.Ready(conn); // from bitbucket OnClientConnect source
    }

    bool PlayerLoggedIn(string player) {
        // in lobby?
        if (lobby.Values.Any(lobbyPlayer => lobbyPlayer.name == player))
            return true;

        // in world?
        if (Entity.teams.Values.Any(team => team.Any(entity => entity.name == player)))
            return true;

        return false;
    }

    void OnServerLogin(NetworkMessage netMsg) {
        print("OnServerLogin " + netMsg.conn);
        var message = netMsg.ReadMessage<LoginMsg>();

        // correct version?
        if (message.version == Application.version) {
            // game not locked yet?
            if (!EveryoneLocked()) {
                // not too long?
                if (message.player.Length <= loginNameMaxLength) {
                    // only contains letters, number and underscore and not empty (+)?
                    // (important for database safety etc.)
                    if (Regex.IsMatch(message.player, @"^[a-zA-Z0-9_]+$")) {
                        // not in lobby and not in world yet?
                        if (!PlayerLoggedIn(message.player)) {
                            print("login successful: " + message.player);

                            // add to logged in accounts
                            lobby[netMsg.conn] = new LobbyPlayer(message.player);
                            BroadcastLobbyUpdate();
                        } else {
                            print("already logged in: " + message.player);
                            ClientSendPopup(netMsg.conn, "already logged in", true);

                            // note: we should disconnect the client here, but we can't as
                            // long as unity has no "SendAllAndThenDisconnect" function,
                            // because then the error message would never be sent.
                            //netMsg.conn.Disconnect();
                        }
                    } else {
                        print("player invalid: " + message.player);
                        ClientSendPopup(netMsg.conn, "forbidden name", true);
                    }
                } else {
                    print("player too long: " + message.player);
                    ClientSendPopup(netMsg.conn, "name too long", true);
                }
            } else {
                print("game already running");
                ClientSendPopup(netMsg.conn, "game already running", true);
            }
        } else {
            print("version mismatch: " + message.player + " expected:" + Application.version + " received: " + message.version);
            ClientSendPopup(netMsg.conn, "outdated version", true);
        }
    }

    // lobby ///////////////////////////////////////////////////////////////////
    // broadcast to everyone in lobby
    void BroadcastLobbyUpdate() {
        foreach (NetworkConnection conn in lobby.Keys)
            conn.Send(LobbyUpdateMsg.MsgId,
                      new LobbyUpdateMsg{players=lobby.Values.ToArray()});
    }

    public bool EveryoneLocked() {
        return lobby.Count > 0 &&
               lobby.Values.All(lobbyPlayer => lobbyPlayer.locked);
    }

    void OnClientLobbyUpdate(NetworkMessage netMsg) {
        var message = netMsg.ReadMessage<LobbyUpdateMsg>();
        print("OnClientLobbyUpdate");

        // hide login, show selection
        if (uiLogin != null) uiLogin.Hide();
        if (uiTeamSelection != null) {
            uiTeamSelection.lobbyMsg = message;
            uiTeamSelection.Show();
        }

        // everyone locked? then show 'loading scene' message
        if (message.players.All(player => player.locked))
            uiStatus.Show("Loading Scene...");
    }

    // called after the client calls ClientScene.AddPlayer with a msg parameter
    public override void OnServerAddPlayer(NetworkConnection conn, short playerControllerId, NetworkReader extraMsg) {
        print("OnServerAddPlayer extra");
        /*if (extraMsg != null) {
            // only while in lobby (aka after handshake and not ingame)
            if (lobby.ContainsKey(conn)) {
                // read the index and find the n-th character
                // (only if we know that he is not ingame, otherwise lobby has
                //  no netMsg.conn key)
                CharacterSelectMsg message = extraMsg.ReadMessage<CharacterSelectMsg>();
                string account = lobby[conn];
                List<string> characters = Database.CharactersForAccount(account);

                // validate index
                if (0 <= message.index && message.index < characters.Count) {
                    print(account + " selected player " + characters[message.index]);

                    // load character data
                    var go = Database.CharacterLoad(characters[message.index], GetPlayerClasses());

                    // add to client
                    NetworkServer.AddPlayerForConnection(conn, go, playerControllerId);

                    // addon system hooks
                    Utils.InvokeMany(typeof(NetworkManagerMMO), this, "OnServerAddPlayer_", account, go, conn, message);

                    // remove from lobby
                    lobby.Remove(conn);
                } else {
                    print("invalid character index: " + account + " " + message.index);
                    ClientSendPopup(conn, "invalid character index", false);
                }
            } else {
                print("AddPlayer: not in lobby" + conn);
                ClientSendPopup(conn, "AddPlayer: not in lobby", true);
            }
        } else {
            print("missing extraMessageReader");
            ClientSendPopup(conn, "missing parameter", true);
        }*/
    }

    // find all available player classes
    public List<Player> GetPlayerClasses() {
        return (from go in spawnPrefabs
                where go.GetComponent<Player>() != null
                select go.GetComponent<Player>()).ToList();
    }

    void OnServerChangeTeam(NetworkMessage netMsg) {
        print("OnServerChangeTeam " + netMsg.conn);
        ChangeTeamMsg message = netMsg.ReadMessage<ChangeTeamMsg>();

        // only while in lobby (aka after handshake and not ingame)
        if (lobby.ContainsKey(netMsg.conn)) {
            LobbyPlayer player = lobby[netMsg.conn];
            if (!player.locked) {
                // change team
                player.team = message.team;
                lobby[netMsg.conn] = player;

                // broadcast to everyone in lobby
                BroadcastLobbyUpdate();
            }
        } else {
            print("OnServerChangeTeam: not in lobby");
            ClientSendPopup(netMsg.conn, "not in lobby", true);
        }
    }

    void OnServerChangeHero(NetworkMessage netMsg) {
        print("OnServerChangeHero " + netMsg.conn);
        var message = netMsg.ReadMessage<ChangeHeroMsg>();

        // only while in lobby (aka after handshake and not ingame)
        if (lobby.ContainsKey(netMsg.conn)) {
            if (0 <= message.heroIndex && message.heroIndex < GetPlayerClasses().Count) {
                LobbyPlayer player = lobby[netMsg.conn];
                if (!player.locked) {
                    // change hero
                    player.heroIndex = message.heroIndex;
                    lobby[netMsg.conn] = player;

                    // broadcast to everyone in lobby
                    BroadcastLobbyUpdate();
                }
            } else {
                print("OnServerChangeHero: invalid hero index: " + message.heroIndex);
                ClientSendPopup(netMsg.conn, "invalid hero index", true);
            }
        } else {
            print("OnServerChangeHero: not in lobby");
            ClientSendPopup(netMsg.conn, "not in lobby", true);
        }
    }

    void OnServerLock(NetworkMessage netMsg) {
        print("OnServerLock " + netMsg.conn);

        // only while in lobby (aka after handshake and not ingame)
        if (lobby.ContainsKey(netMsg.conn)) {
            LobbyPlayer player = lobby[netMsg.conn];
            if (!player.locked) {
                // lock
                player.locked = true;
                lobby[netMsg.conn] = player;

                // broadcast to everyone in lobby
                BroadcastLobbyUpdate();

                // was this the last player to lock?
                if (EveryoneLocked())
                    // change scene for everyone
                    ServerChangeScene(playScene);
            }
        } else {
            print("OnServerLock: not in lobby");
            ClientSendPopup(netMsg.conn, "not in lobby", true);
        }
    }

    // called after the client loaded a scene
    public override void OnClientSceneChanged(NetworkConnection conn) {
        Debug.Log("OnClientSceneChanged");
        // call base function to guarantee proper functionality
        // -> this sets conn.isReady = true.
        base.OnClientSceneChanged(conn);

        uiStatus.Show("Waiting for other players...");
    }

    // OnServerReady is called when a client finished loading a scene
    public override void OnServerReady(NetworkConnection conn) {
        Debug.LogWarning("OnServerReady: " + conn);

        // call base function to guarantee proper functionality
        // -> this sets conn.isReady = true.
        base.OnServerReady(conn);

        // are they ready because of the lobby scene was loaded, or the world scene?
        if (EveryoneLocked()) {
            // if last one connected then spawn them all
            if (lobby.Keys.All(connection => connection.isReady)) {
                Debug.LogWarning("ALL RDY");

                foreach (KeyValuePair<NetworkConnection, LobbyPlayer> kvp in lobby) {
                    LobbyPlayer lobbyPlayer = kvp.Value;

                    // create gameobject
                    GameObject prefab = GetPlayerClasses()[lobbyPlayer.heroIndex].gameObject;
                    PlayerSpawn spawn = FindObjectsOfType<PlayerSpawn>().Where(g => g.team == lobbyPlayer.team).First();
                    GameObject go = GameObject.Instantiate(prefab, spawn.transform.position, Quaternion.identity);
                    go.name = lobbyPlayer.name;
                    go.GetComponent<Player>().team = lobbyPlayer.team;

                    // add to client
                    NetworkServer.AddPlayerForConnection(kvp.Key, go, 0);
                    print("spawned " + lobbyPlayer.name + " (" + lobbyPlayer.team + ") at " + spawn.name);
                }
            }
        }
    }

    // stop/disconnect /////////////////////////////////////////////////////////
    // called on the server when a client disconnects
    public override void OnServerDisconnect(NetworkConnection conn) {
        print("OnServerDisconnect " + conn);

        // TODO save player in case of accidentally disconnecting so we can
        // reconnect later

        // was this player in the lobby? then let everyone else know that he left
        if (lobby.ContainsKey(conn)) {
            lobby.Remove(conn);
            BroadcastLobbyUpdate();
        }

        // do base function logic without showing the annoying Debug.LogErrror
        // (until UNET fixes it)
        //base.OnServerDisconnect(conn);
        NetworkServer.DestroyPlayersForConnection(conn);
    }

    // called on the client if he disconnects
    public override void OnClientDisconnect(NetworkConnection conn) {
        print("OnClientDisconnect");

        // show login mask again
        uiLogin.Show();

        // call base function to guarantee proper functionality
        base.OnClientDisconnect(conn);

        // call StopClient to clean everything up properly (otherwise
        // NetworkClient.active remains false after next login)
        StopClient();
    }

    // universal quit function for editor & build
    public static void Quit() {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // called when quitting the application by closing the window / pressing
    // stop in the editor
    // -> we want to send the quit packet to the server instead of waiting for a
    //    timeout
    // -> this also avoids the OnDisconnectError UNET bug (#838689) more often
    void OnApplicationQuit() {
        if (IsClientConnected()) {
            StopClient();
            print("OnApplicationQuit: stopped client");
        }
    }

    void OnValidate() {
        // Channel #0 always has to be reliable and Channel #1 always has to be
        // Unreliable because Channels.DefaultReliable is 0 and
        // DefaultUnreliable 1.
        //
        // Those two helpers are used by the HLAPI and by us to avoid hardcoding
        // channel indices for NetworkBehaviours, Commands, Rpcs, etc.
        //
        // Changing them would result in chaos. Using anything != Fragmented
        // would fail to send bigger messages too.
        if (channels.Count < 1 || channels[0] != QosType.ReliableFragmented)
            Debug.LogWarning("NetworkManager channel 0 has to be ReliableFragmented");
        if (channels.Count < 2 || channels[1] != QosType.UnreliableFragmented)
            Debug.LogWarning("NetworkManager channel 1 has to be UnreliableFragmented");
    }
}
