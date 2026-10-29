# Networking: local games, the relay and direct paths

29 October 2026. How Genesis's multiplayer moves messages, from a game on one network to players
who meet through a relay and then reach each other directly (NAT punch-through).

## The parts

| Part | Where | What it does |
|---|---|---|
| `IGameNetwork` and its side interfaces | `Source/Runtime/Genesis.Shared/Net` | What game code sees: host, connect, send, broadcast, peers, tagged messages. `IGameNetworkDiscovery` (finding games on the local network), `IGameNetworkStatus` (connection state, last error), `IGameNetworkDirect` (direct paths). |
| `LiteNetGameNetwork` | `Source/Runtime/Genesis.Net` | The one transport, on LiteNetLib (reliable UDP). `LiteNetGameNetwork.cs`: local games and discovery; `.Relay.cs`: games through a relay; `.Direct.cs`: direct paths. |
| `GenesisRelayServer` | `Source/Runtime/Genesis.Net` | The relay: the Player run as `GenesisEngine.exe --relay 7777`. Sessions by code, forwarding, introductions. |
| `NetworkReplication` | `Source/Runtime/Genesis.Runtime/Net` | Shared objects with interest distance; rides on `Send` with negative tags (-7001 to -7014). |
| PGSL | `PgslCommands.Systems.cs`, `.NetworkLobby.cs`, `.NetworkOnline.cs`, `.NetworkDirect.cs` | `Net*` commands and `Engine.Network.*` (Documentation/EngineApi.md). |

Tags below zero belong to the engine: the relay's frames are -1001 to -1022, the direct paths' stream
-1201 and -1202, replication -7001 to -7014. Scripts use positive tags.

## Local games

A host binds a UDP port; players connect by address (`NetConnect`) or find hosts first
(`NetDiscover`). One LiteNetLib connection per player, so ordering and reliability are LiteNetLib's.

## Through a relay

Over the internet routers stand in the way, so both sides connect out to a relay they can reach.
The host asks for a session and is given a six-character code; players join with it. The relay
passes each game frame on (`Forward` to `Forwarded`, keeping its delivery method), so a reliable
ordered message from one machine reaches the other in order: both legs are LiteNetLib reliable
ordered channels and the relay forwards in arrival order. Players talk to their host only (a star).

| Frame | Way | Body |
|---|---|---|
| HostRequest -1001 | game → relay | int max players |
| Hosted -1002 | relay → host | string code |
| JoinRequest -1003 | game → relay | string code |
| Joined -1004 | relay → player | int host id |
| Refused -1005 | relay → game | string reason |
| PeerJoined -1006 / PeerLeft -1007 | relay → session | int peer id |
| Forward -1010 | game → relay | int target (0: every other member), int tag, int length, bytes |
| Forwarded -1011 | relay → game | int source, int tag, int length, bytes |
| DirectOffer -1020 | game → relay | bool willing |
| Introduce -1021 | relay → game | int peer id, string address, int port, long secret |
| IntroduceAgain -1022 | host → relay | int peer id |

## Direct paths (NAT punch-through)

The relay's distance adds to every message's time. Most home routers allow a direct path if both
sides send first, so players who meet through the relay try to open one, and move their traffic to
it when they can.

1. **Offer.** A game whose `Engine.Network.Direct.Enabled` is true (the default) sends `DirectOffer`
   right after its host or join request. The relay introduces only two games that both offered, so a
   game that turns it off never has its address given out.
2. **Introduction.** When a player joins (or offers later), the relay sends the host and the player
   each other's UDP address as the relay sees it, which is the address and port the player's router
   gives the outside world, plus a fresh 64-bit secret from a cryptographic random source.
3. **Punching.** Both send a small unconnected packet (`genesis-punch`: application key, secret,
   "I have heard you") to that address ten times a second, from the same socket they reach the relay
   on, so the router keeps the mapping the relay saw. A packet out opens the sender's own router for
   the reply. A packet heard is answered at once, and its source becomes the address used (a router
   may map the path to a new port). A try lasts `Engine.Network.Direct.Timeout` seconds (5).
4. **Open.** When a side has heard the other and been told it was heard, packets pass both ways.
   The player then connects to the host at that address with LiteNetLib, the connection request
   carrying `genesis-direct` and the secret; the host accepts only a secret it is trying with. Once
   connected, both sides send this player's traffic on the direct connection.
5. **Gave up.** A try that runs out leaves everything on the relay, exactly as before. The host asks
   the relay to introduce the two again (`IntroduceAgain`) ten seconds after a failed try or a lost
   path, until `Engine.Network.Direct.Attempts` (3) tries are used; the relay stops at 32 per player.

Router kinds: full cone (one public port, anyone may send in once the game has sent), restricted
(only addresses sent to) and port restricted (only the address and port sent to) all open, because
each side sends to the other's mapped address first. A symmetric router gives each destination a new
port, so the address the relay saw is useless to the other player and its packets are turned away:
the try runs out and the players stay on the relay. One symmetric side facing an open or full-cone
side still meets, because the open side answers wherever the packets came from.

### Nothing lost or reordered when the path changes

- From the first switch to a direct path, every reliable message to that player (ordered or not) is
  **numbered** and sent as a stream frame (tag -1201: int number, int tag, int length, bytes) on
  whichever path is open. The receiver hands them on in number order, keeps early ones (up to 4096),
  and drops any number it already had.
- **Number 0** (tag -1202 inside, never handed to the game) goes **through the relay**, behind every
  plain message sent there before the switch. Numbers 1 on go direct. So a direct message cannot
  overtake one still on its way through the relay: it waits for number 0.
- Numbered messages sent directly are kept until LiteNetLib reports them delivered
  (`IDeliveryEventListener`; reliable ordered, so one delivery covers every earlier number).
- **Losing the direct path**: the transport's disconnect, or no packet for half its time-out (2.5 s;
  it pings every second). Everything numbered that was sent directly and not delivered is sent again
  through the relay, in order, and later messages follow it there. The receiver drops what it had.
- A later switch back to direct needs no marker: the numbers already order both paths.
- **Unreliable** messages (replication's movement) take whichever path is open and promise nothing,
  as before: what was sent into a path that died is gone, and the next update puts it right.
- A message keeps its sender's peer id whichever path it came by, so games and replication see one
  player throughout. The host's broadcast becomes one send per player once any player is numbered.

### Where it stops

- **The session lives at the relay.** Losing the relay ends the session for everyone, direct paths
  included, as it did before.
- **Players behind the same router** reach each other only if it supports hairpinning; there is no
  local-address candidate yet. Such players can play as a local game instead.
- The punched path uses the address the relay saw, IPv4 or IPv6; only IPv4 is covered by the tests.
- Each player's public address is given to the players it plays with (only when both offered).
  `Engine.Network.Direct.Enabled = false` keeps a player's address private, at the relay's latency.
- A relay or a game from before 29 October ignores the new frames, so a mix stays on the relay.
- No relay-server change is needed by games that never use it; local games never see any of this.

## Settings (Engine.Network.Direct)

| Value | Default | Meaning |
|---|---|---|
| `Enabled` | true | Try for direct paths. True by default because the relay fallback keeps today's behaviour wherever a direct path cannot open. |
| `Active` (read) | "" | "direct" when every player is reached directly, "relay" otherwise, "" when not playing through a relay. |
| `Attempts` | 3 | Tries per player, 1 to 10. |
| `Timeout` | 5 | Seconds one try punches before it gives up, 0.5 to 60. |
| `Count` (read) | 0 | Players reached directly now. |
| `Path(peerId)` | | "direct" or "relay" for one player (-1: a player's host); "" otherwise. |

They are room settings in the Inspector like every settable `Engine.*` value. The Network area is not
on the F6 debug screen.

## Tests

`--test network-online` (`Tests/Genesis.Application.Headless/Suites/NetworkOnlineSuite.cs`) runs on
127.0.0.1 only: no public port, no outside server. `LoopbackNat` is a stand-in router: the game's
LiteNetLib packet layer sends everything to the router's inside socket with the real destination
appended, and the router sends it on from its own public socket(s), letting packets in only as a
full-cone, restricted, port-restricted or symmetric router would. The cases: the relay by code; full
cone opens a direct path, ordered messages and a shared object carry on across the switch, the relay
forwards nothing while it is open, and cutting it falls back within about 2.5 s with nothing lost;
port restricted opens (and still turns a stranger away); symmetric on both sides tries once and stays
on the relay; a symmetric host facing a full-cone player meets; a cut path is tried again after the
pause and reopens, numbers carrying on in order; turned off, the relay introduces no one and carries
everything; the Engine.Network.Direct names and Inspector settings. `--test engine-api-parts` sweeps
Engine.Network.Direct with every other part (its settings read their default there, with no network,
so they are listed as known silent beside Engine.Network.Replication.Rate).
