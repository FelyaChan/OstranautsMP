# Ostranauts Multiplayer (Direct IP)

> ⚠️ **Work in progress.** Expect bugs and desyncs. Back up your saves.

Two-player co-op for **Ostranauts**. One player hosts, the other joins by IP. The host runs the game; the client's world mirrors it.

* Made for **Ostranauts v1.0.1.8a**, BepInEx **5.4.23**. Newer builds aren't tested.
* Host and client need the **same mod version**; otherwise the connection is refused and both are told.
* 📜 [Changelog](CHANGELOG.md) · 🐞 Report bugs: [Discord](https://discord.gg/TnVpW7yBxG) · [GitHub](https://github.com/FelyaChan/OstranautsMP)

---

## Install (both players)

1. Install **BepInEx 5.x (64-bit)**: extract it into the game folder (next to `Ostranauts.exe`), run the game once, close it.
2. Put `OstranautsMP.dll` into `Ostranauts/BepInEx/plugins/`.

```text
Ostranauts/
├── Ostranauts.exe
├── winhttp.dll
└── BepInEx/plugins/OstranautsMP.dll
```

## Play

**Both:** on the main menu console, click **MULTIPLAYER** (below **OPTIONS**). Make your character there first: **Create character**.

**Host**
1. Enter your name, keep port `7777`, click **Host**, then **Close**.
2. Load a save (**CONTINUE** or the saves list). *New games aren't supported.*
3. Let your friend reach you: forward TCP `7777` on your router, or use **Radmin VPN** / **Hamachi**. Share your IP.

**Client**
1. Enter your name, the host's IP and port, click **Join**. The save downloads and loads by itself.
2. In the game, take a **New body** (made from your profile) or pick one of the host's crew (not the host's character). That character is yours from then on.

The host's game stays paused while the client loads, including when the client joins mid-game. **Unpause when they're in.**

## In the game

| What | How |
|---|---|
| Talk | **Enter** → type → **Enter** (speech bubble) |
| PDA message | `/pda text`, or `/pda` alone to switch the field. Read in the **CHAT** app on the PDA |
| Ping | `~` on a spot, item or person |
| Pause both | **Space** |
| Give item, offer money, follow, talk | right-click the other player's character |
| Emotes, play dead | right-click your own character (**Emote**) |
| Change looks / name | **View Mirror** at a sink |
| Mark a locker yours | right-click a container → **Make it mine** / **Give it up** (a label only) |
| Money | **BUDGET** app: the host sets salary and gives money; the client sees their wallet |
| Profile, bio, training | **PROFILE** app on the PDA |
| Chat commands | `/pda`, `/me`, `/sync`, `/fixships` (listed when the field opens) |

## Features

**Sync.** People's positions and animations, needs, injuries and moods, room atmosphere, time, items, inventory and structures, with automatic desync detection and repair. The client's orders and menu actions run on the host; the client sees them at once, but their results (damage, items, things breaking) come from the host. NPC AI, story events and bills run on the host only.

**Session.** Join mid-game; reconnect from the same address takes the old place at once. A lost connection pauses the game on both sides and says so.

**Ships** *(experimental)*. Docking and undocking without a reload, synced flight (the client can pilot), host-decided collisions, AI traffic mirrored.

**Apart** *(experimental)*. When the host leaves the client behind, the client's place keeps running on the client. The host takes a copy every few minutes (`ApartSyncMinutes`, default 3), on `/sync`, and before coming back. While apart, time skips need the client's OK and the client can't travel.

**Your character** *(experimental)*. Profile made in the menu (looks, name, pronouns, up to 5 skills) becomes your avatar and your new bodies. On death you watch and take a new body when the host's ship is in port, with 80% of the training. Your character can't quit the crew.

**Together.** Dialogue between players both ways; give items, offer money ($10 / $100 / $250), follow, emotes, play dead *(experimental)*. Optional **Highlights**: your lockers glow under the mouse, the other player has a faint aura while you can see them. The client can trade with their own money *(experimental)*.

## Files

Kept in `BepInEx/config`: settings (`fcona.ostranauts.multiplayer.cfg`), your profile and avatar, PDA chat history. Logs: `BepInEx/OstranautsMP_<n>.log`, one per running game (attach it to bug reports).
