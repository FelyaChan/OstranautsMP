# Changelog

All notable changes to the Ostranauts Multiplayer Mod.
Host and client must run the same version.

## [0.24.3] - 2026-11-05

### Changed
- The host takes the client's place on request with `/sync` in chat. This replaces the **Sync their place now** button: the Multiplayer window only opens in the main menu, so the button couldn't be reached while playing.

## [0.24.2] - 2026-11-05

### Added
- The client can pause and unpause the game for both players with the pause key (Space). The chat says who did it. Windows that pause the game by themselves still don't pause the host.

### Fixed
- The client saw a long line, or a bar dropping back to zero, over people doing something. Each person has their own clock, set differently on each machine. The bar now starts from the time already elapsed, not from the host's clock. The long bar (work left on the target) also comes from the host.

## [0.24.1] - 2026-11-05

### Added
- Optional highlights, a **Highlights** checkbox in the Multiplayer window (on by default):
  - your own locker lights up in your ping colour under the mouse;
  - the other player's character has a faint aura in their colour while your character can see them. Walls and closed doors block sight, glass doesn't. The aura fades in and out.
- Players share their ping colour with each other.

## [0.24.0] - 2026-11-05

### Added
- Change your character's looks and name at a sink's mirror (**View Mirror**). In multiplayer it opens the whole face, skin, body, random looks and the name field. The game normally offers only hair, beard, glasses and scars there.
  - Only for your own character: the host can't open it for the client's character, and the client can't open it for the host's.
  - The changes go to the other player when the window closes.
  - Renaming happens once, on closing. It updates everything keyed by the name: ID, identity condition, relationships, owned ships, crew roster, ledger entries (salary), lockers, and the mod's own references.
  - Character creation (life path) is unchanged.
  - Random looks don't repaint relatives outside character creation.

## [0.23.1] - 2026-11-05

### Added
- Locker ownership. A player marks an installed container as theirs with **Make it mine**, and releases it with **Give it up**.
  - It's a label only: the name shows "(owner)".
  - It is saved with the ship and kept when the item changes kind.

## [0.23.0] - 2026-11-05

### Added
- The client sees progress bars over people working: repairing, installing, eating.

### Fixed
- An item that changed kind on the host (repaired, broken, switched on or off) stayed the old kind on the client. It only offered Bash and Pick up. The host now sends it in full, and the client rebuilds it.
- The client's character's needs drifted from the host's. Food the client ate came back, and the host refused actions the client had chosen. The host now sends that character's conditions in full every 3 seconds.

## [0.22.1] - 2026-11-05

### Added
- Pings on a person name them, ring them and follow them.

### Fixed
- After the host came back to the client's place, the client's character and the NPCs there were missing on the host. The game keeps people on an unloaded place as live objects, and they were wrongly kept out of the client's copy.
- Money the host gave the client's character while apart (or salary paid then) never reached the client.

## [0.22.0] - 2026-11-05

### Added
- **Apart mode.** When the host leaves the client behind (transit, undocking), the client's place keeps running on the client:
  - its NPCs, the client's orders, dialogue and trade all happen there;
  - the host's characters left in it are hidden and still.
- The host takes a copy of the client's place:
  - every few minutes (`ApartSyncMinutes`, default 3);
  - on request;
  - always just before the host's game loads it again, so coming back finds it as the client left it.
- While apart:
  - speeding up or skipping time needs the client's agreement;
  - the client can't travel;
  - NPCs don't move into or out of the client's place on the host.
- Undocking with the client on the other side keeps the client's side loaded.

### Fixed
- Crash on the client when the host changed location. Two big operations ran on top of the game's own memory use and ran it out of memory:
  - the client saving the game by itself;
  - the client reloading the whole save.
- The client no longer saves the game itself.
- The client no longer reloads the whole save when the host leaves it behind.
- The host only sends the client what it has loaded. This ends the "Desync" and "Dropped item update" floods.

## [0.21.0] and earlier

- Direct-IP host/join from a **MULTIPLAYER** button on the main menu console; save transfer and joining mid-game.
- The client plays a crew member. Synced:
  - positions, animations and conditions;
  - room atmosphere and game time;
  - items, with desync detection and repair.
- Ships:
  - docking and undocking without a reload;
  - the host's space mirrored on the client;
  - client piloting;
  - host-only collisions;
  - epoch-corrected ship positions.
- Chat:
  - typewriter bubbles with names coloured by the speaker's state;
  - `/me` actions;
  - pings on the `~` key, with a choice of colour.
- **BUDGET** PDA app:
  - salary through the game's ledger;
  - giving money;
  - client trade relayed to the host.
- Player-to-player interactions:
  - dialogue reply sync;
  - give an item, offer money, follow;
  - emotes, play dead.
- The client's NPC AI and story events are off on the client. Caps Lock and crew switching are blocked on the client.
