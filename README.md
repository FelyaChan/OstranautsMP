# Ostranauts Multiplayer Mod (Direct IP)

> ⚠️ **WIP (Work In Progress):** This mod is in early development. Expect bugs, synchronization issues, and potential crashes. Use with caution and backup your saves!

A client-server multiplayer mod for **Ostranauts** utilizing an authoritative listen server model. One player acts as the Host (Server), and another connects as the Client via a direct IP address.

Made for Ostranauts v1.0.1.8a  
Not been tested on more recent builds.

## How it works
* **Authoritative Host:** The host manages game logic, simulation, and world state. The client's world mirrors the host's.
* **Direct Connect:** Connection is established via TCP/IP directly between players.

---

## Features

### Connection & session
* Host / Join from a **MULTIPLAYER** button on the main menu console, with a menu-styled window.
* Remembers your player name, the last IP and the port.
* The host's save is sent to the client automatically on load; a client can **join mid-game** (the host pauses, autosaves and sends the save).
* Keepalive and timeout detection; "joined / left / timed out" notices in chat.
* Version check between host and client.

### Playing together
* The client picks a crew member and plays them; that character is marked as a multiplayer character in the save.
* Game speed is controlled by the host; either player can pause and unpause for both (Space).
* Synced: positions and animations of all people, needs/stats/injuries/moods, room atmosphere (gas, pressure, temperature), in-game time.
* Client orders and radial-menu actions are carried out by the host.
* Items, inventory, equipment and structures are synced, with automatic desync detection and repair.
* Clicking the host's character as the client doesn't take your character away; Caps Lock crew switching is disabled for the client.
* The client's world is a mirror of the host's: NPC AI, story events, random encounters and recurring bills run on the host only.
* Progress bars over working people (repairs, installs, eating) are shown on the client too.

### Apart mode *(experimental)*
* When the host leaves the client behind (transit, undocking), the client's place keeps running on the client's side: its NPCs, the client's orders, dialogue and trade.
* The host takes a copy of the client's place every few minutes (`ApartSyncMinutes` in the config, default 3), on request (`/sync` in chat), and always right before coming back, so nothing is rolled back.
* Money the host gives (or salary paid) while apart reaches the client.
* While apart, speeding up or skipping time needs the client's agreement, and the client can't travel.

### Ships & space
* Docking and undocking without reloading the save (the docked ship is sent over). *(experimental)*
* Ship flight is synced; the client can also pilot (manual helm) with immediate local response. *(experimental)*
* Space mirrors the host: AI traffic, newly spawned and removed ships; collisions are decided by the host. *(experimental)*
* Undocking with the client on the other side keeps the client's side running (apart mode).
* Falls back to resending the save if a location change can't be synced otherwise.

### Communication
* Chat: a window in the main menu and a **CHAT** app on the PDA (game-styled), with in-game timestamps.
* Speech bubbles over characters, typed out typewriter-style with the speaker's name; off-screen bubbles point to the speaker.
* Player names are coloured by how the character is doing (green → red).
* **Ping** with the `~` key: marks a spot or an item under the cursor with its name; on a person it rings them and follows them. Visible to both, with an edge arrow when off-screen. Each player picks their ping colour.
* **Highlights** (optional, a checkbox in the Multiplayer window): your own locker lights up in your colour under the mouse; the other player's character has a faint aura in their colour while your character can see them (walls and closed doors block sight).

### Economy
* **BUDGET** PDA app on the host: set the client character's daily salary (paid through the game's finance screen) and give money.
* The client sees their own wallet (also on the bottom cash button) and ledger in their **BUDGET** app.
* The client can buy and sell at traders with their character's own money. *(experimental)*
* The client's character can't quit the crew.

### Your character
* **Mirror:** at any sink, **View Mirror** lets you change your own character's whole look (face, skin, body) and name. The other player sees the change. Each player can only edit their own character. Renaming keeps skills, items, salary and crew membership. *(experimental)*
* **Lockers:** **Make it mine** on an installed container (locker, cabinet, fridge) adds your character's name to it; **Give it up** removes it. It's a label only, nobody is locked out.

### Player-to-player interactions (in the game's own dialogue menu)
* Dialogue between players works both ways; the client's character answers by the player's choice.
* **Give item in left / right hand** → the other player accepts (needs a free hand) or refuses. *(experimental)*
* **Offer money** → if accepted, the giver picks $10 / $100 / $250. *(experimental)*
* **Follow** the other player. *(experimental)*
* Emotes on your own character: nod, shake head, gesture, clap, get angry, hands up, show a trick, squat, guard stance, check tablet, lie down. *(experimental)*
* **Play dead** — the other player can drag you; you're not actually unconscious or harmed. *(experimental)*

---

## How to Play

**Both players** need the same version of the game and the same version of the mod (the Multiplayer window shows a warning on a version mismatch).

### As the Host:
1. Launch the game and click the purple **MULTIPLAYER** button on the lower part of the main menu console (below **OPTIONS**).
2. Enter your Player Name, keep the port at `7777` (default), and click **Host**.
3. Click **Close** to close the window; you stay hosting.
4. **Load your save** with **CONTINUE** or from the saves list (**Note:** the mod currently *does not work with creating a new game*).
5. Make sure your crew has **at least one more member** besides your character — that's who the client will play.
6. Open port **7777** (TCP) on your router (Port Forwarding), or use virtual network software like **Radmin VPN** / **Hamachi** so your friend can reach your PC.
7. Share your IP address with your friend.

If the client joins while you're already in the game, the game pauses and sends them the save. **Unpause when they're in.**

### As the Client:
1. Launch the game and click the **MULTIPLAYER** button on the main menu console.
2. Enter your Player Name.
3. Enter the Host's IP address and port (`7777`), then click **Join**.
4. The window stays open while you wait: the host's save is downloaded and loaded automatically.
5. In the game, a **Choose your character** window appears: pick a crew member. This character becomes yours permanently (they also can't quit the crew).

### In the game
* **Talk, give items, offer money, follow:** right-click the other player's character and use the dialogue / actions menu. Replies to the other player show up in the same menu.
* **Emotes and Play dead:** right-click your own character (the **Emote** group); **Get up** to stop playing dead.
* **Ping:** press `~` (the key left of `1`) — on a spot, a person or an item.
* **Pause:** Space pauses or unpauses the game for both players.
* **Change your looks and name:** use **View Mirror** on a sink. The new name is applied when you close the window.
* **Lockers:** right-click an installed container → **Make it mine** / **Give it up**.
* **Apart:** if the host leaves your place, it keeps running for you, and the host gets it back as you left it. The host can take it right away with `/sync` in chat.
* **Chat:** the **CHAT** app on the PDA (in the main menu: the **Chat** button in the Multiplayer window).
* **Money:** the **BUDGET** app on the PDA — the host sets salary and gives money; the client sees their wallet and ledger. Salary is paid by the host in the game's finance screen (the cash button at the bottom).
* **Piloting:** game speed is the host's. Either player can fly from the nav station; if both steer, the host's input wins.

---

## Installation Guide

Both players install the mod.

### Step 1: Install BepInEx
1. Download **BepInEx 5.x (64-bit)** (Recommended version: `5.4.23`).
2. Extract all contents of the downloaded ZIP archive directly into the main Ostranauts game directory (where `Ostranauts.exe` is located).
3. Run the game once and close it. This will automatically generate the required folder structure (including the `plugins` folder).

### Step 2: Install the Mod
1. Download the latest release of this mod (`OstranautsMP.dll`).
2. Move the `OstranautsMP.dll` file into the `Ostranauts/BepInEx/plugins/` directory.

### Final Folder Structure:
```text
Ostranauts/
├── Ostranauts.exe
├── winhttp.dll
└── BepInEx/
    ├── core/
    └── plugins/
        └── OstranautsMP.dll
```
