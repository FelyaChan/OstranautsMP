# Changelog

All notable changes to the Ostranauts Multiplayer Mod.
Host and client must run the same version.

## [0.33.1] - 2026-11-06

### Fixed
- Client: the claimed character (a new body too) couldn't be walked. After claiming, nothing was selected, so a click on the floor went to the host's character and was blocked. The character is now selected right after claiming and after every load, and with nothing selected, clicks go to them.

## [0.33.0] - 2026-11-06

### Changed
- Main menu: bigger text, buttons and windows in the Multiplayer window and the character editor (Looks and Skills). The windows stay on the screen.

## [0.32.0] - 2026-11-06

### Added
- **Delete character** in the Multiplayer window (main menu, while not connected). To confirm, type the character's name. The profile's looks, name, skills and avatar are deleted, and the profile gets a new ID: crew members the old character had in saves are no longer yours (others see them as another player's).

## [0.31.1] - 2026-11-06

### Changed
- A new body starts with no money (the game used to give a new crew member a random amount). The host gives money and sets a salary in the BUDGET app.

## [0.31.0] - 2026-11-06

### Added
- **New body:** in the **Choose your character** window the client can take a new body instead of a crew member. The host makes a new crew member next to a crew spawn point of their ship, with the profile's looks, name and skills, and the client plays them. Shown while the profile has nobody in this game; after that the client picks that character as before.
  - The new body joins the host's crew without a contract: no signing bonus, salary or death pay. The host can set a salary in the BUDGET app.
  - Skills: exactly the profile's (the game's random ones are taken away). Traits and starting things are the game's usual ones for a new crew member.
- Character profile: up to 5 skills.

### Fixed
- People who appear on the host while the client is in the game (hired, brought by a story or an encounter, a new body) now appear on the client too. Before, the client didn't have them, and their things hung around as "missing".

## [0.30.0] - 2026-11-06

### Added
- Character profile: a **Skills** tab in the editor (Create / Edit character in the Multiplayer window). It lists the game's skills; tick the ones your character should have. The choice is saved with the profile (`Skills` in `OstranautsMP_profile.json`).

### Notes
- The skills aren't applied in the game yet: picking a crew member works as before. They're for the new player body, which comes next.

## [0.29.0] - 2026-11-05

### Added
- PDA message notification: when the other player writes to the PDA chat and it isn't open, a box with their avatar, name and the start of the text shows in the bottom right for a few seconds, with a sound. Click it to open the chat.

### Fixed
- Client: the work progress bar over a person sometimes disappeared while the host's work went on. The client's own AI no longer turns off bars the host runs; only the host does, or the bar running out.

## [0.28.0] - 2026-11-05

### Added
- While the client loads a save the host sent, the host's game stays paused and can't be unpaused. A panel at the top shows the progress: sending the save, then the client's loading screen. When the client is in the game, the panel goes away and the game can be unpaused. Disconnecting ends the wait too.

## [0.27.1] - 2026-11-05

### Changed
- PDA chat: bigger messages, avatars and text.
- Quick chat: bigger command suggestions.
- Mod windows: the title (MULTIPLAYER, CHAT…) sits at the top edge of the window.

## [0.27.0] - 2026-11-05

### Changed
- PDA messages are kept with your profile on your computer, not in the save: one history across all games and sessions, the main menu chat included. Each player keeps their own copy; deleting only affects yours.
- The CHAT app shows each sender's profile avatar. Players send their avatar to each other on connect and when their looks change; received avatars are kept, so old messages keep them.
- Separator lines use in-game minutes within one game and real minutes otherwise.

### Notes
- Files in `BepInEx/config`: `OstranautsMP_pda.txt` (the messages, last 200) and the `OstranautsMP_avatars` folder (other players' avatars).
- Messages saved into characters by 0.26.1 stay in those saves but are no longer shown.

## [0.26.1] - 2026-11-05

### Added
- PDA messages are kept with each player's character in the save. The host's save holds both players' logs; deleting on the client deletes in the saved copy too.
- The CHAT app shows each sender's portrait next to their messages, like a messenger.

### Notes
- Notices (joined, left, apart…) are shown in the PDA for the session only, they aren't saved.

## [0.26.0] - 2026-11-05

### Changed
- Two kinds of chat:
  - **Speech:** Enter in the game. Your character says it in a speech bubble. It isn't kept anywhere.
  - **PDA messages:** `/pda text` in the quick chat, or switch the quick chat to PDA mode (`/pda` alone, or the SAY/PDA button next to the field). The main menu chat and notices go there too.
- The quick chat lists its commands when it opens and filters them as you type a `/`. Click one or press Tab to use it.
- The PDA **CHAT** app is now a list of records:
  - each message shows the sender's colour and name, the in-game time and the text;
  - a line separates messages more than 5 in-game minutes apart;
  - each message can be deleted, and **Delete chat** clears them all;
  - nothing is typed in the app itself.

## [0.25.6] - 2026-11-05

### Changed
- In the game, chat lines and speech bubbles are under the speaker's character's name instead of the player's name. The player's name is still used in the main menu chat and in join/leave notices.

## [0.25.5] - 2026-11-05

### Fixed
- Quick chat didn't open for a host with nobody connected yet. It now works whenever you host or have joined.

## [0.25.4] - 2026-11-05

### Added
- Quick chat: **Enter** in the game opens a one-line field at the bottom of the screen. Enter says it (a speech bubble, and a line in the chat), Esc closes it. `/me` and commands work there too.

## [0.25.3] - 2026-11-05

### Changed
- A client's character is crew and never owns a ship. Ships registered to one go back to the host's character by themselves whenever the host loads a game, and the chat says so. `/fixships` still does it on demand.

## [0.25.2] - 2026-11-05

### Fixed
- Renaming a crew member (mirror, profile) registered to them every ship they lived on, the captain's included. The host then lost the ship, and the nav station asked to contact the office. Now only ships actually registered to the renamed person follow the name.
- Renaming now also updates faction member lists.

### Added
- `/fixships` in chat (host): ships registered to a client's character go back to the host's character. This repairs saves affected by the bug above.

## [0.25.1] - 2026-11-05

### Added
- Changes the client makes to their own character at a mirror in the game go back into their profile and avatar, so the next join keeps them.

## [0.25.0] - 2026-11-05

### Added
- Character profile, made in the main menu: **Create / Edit character** in the Multiplayer window.
  - Face part by part (as in the game's own editor), skin, random face, body type, pronouns, attraction, name.
  - A flat portrait preview, saved as your avatar and shown in the Multiplayer window.
  - Saved on your computer: `BepInEx/config/OstranautsMP_profile.json` and `OstranautsMP_avatar.png`.
- When you join and pick a crew member, they become your profile's character: looks and name. Their skills, traits and items stay theirs.
- A crew member picked with a profile belongs to it. Nobody else can pick them; you can again next time. A crew member a client played before profiles existed goes to the first profile that picks them.

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
