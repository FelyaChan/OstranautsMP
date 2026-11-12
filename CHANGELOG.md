# Changelog

All notable changes to the Ostranauts Multiplayer Mod.
Host and client must run the same version.

## [0.41.6] - 2026-11-11

### Fixed
- Client: an AI ship that leaves by docking and disappearing (the tug that brings a ship in) disappears for the client too, undocking first if it was docked to the client's ship.

## [0.41.5] - 2026-11-11

### Fixed
- Client: an item check that the host's fix doesn't clear is asked again only 3 times, until something changes, instead of every 10 s. The host's log tells where such an item really is.

## [0.41.4] - 2026-11-11

### Changed
- Radio: a line takes 0.1% of the suit battery instead of 1%; its hiss and static are 30% quieter.

## [0.41.3] - 2026-11-11

### Fixed
- Client: actions run there by others than the client's character (breathing in vacuum, putting items on, zone triggers, ship messages) no longer make their own items or random outcomes; only their condition changes apply there, the rest comes from the host. A person in vacuum got their own scare items on the client.

## [0.41.2] - 2026-11-11

### Fixed
- Fixed doppelgänger entity.

## [0.41.1] - 2026-11-11

### Added
- Logs for crashes and disconnects: a note at startup when the last run didn't end normally (with where Unity's log and crash report are), a network line every 30 s (every 5 s when slow), a report of why a connection ended, the other side's clock to line up both logs, and errors from background threads.

## [0.41.0] - 2026-11-08

### Added
- Speech sounds: each letter of a speech bubble makes a short synthesized sound from the speaker, in 3D and through the game's world sound (quieter far away, muffled in a helmet, silent in vacuum; the effects volume sets it). **Speech effects** in the Multiplayer window turns it off.
- Voice in the character editor (**Voice** tab): pitch and speed, with **Listen** to hear it. The speed also sets how fast your bubbles type.
- A "..." over a player's character while they type in the quick chat, for both players.
- Client: an alert when the host starts saving.
- PDA chat: a field to write in (Enter or **Send**), and a sound for every message received, chat open or not.
- Radio: in a helmet with a charged suit battery, speech goes over the suit radio, with its own plate and sound. It reaches two or three rooms; each line takes a little of the battery; below half charge, at the edge of its range or through the hull, letters are lost to static. Out loud, speech is only heard in the same room or in sight of the speaker. In a helmet without power, or with no air around, nobody hears it: others only see the lips move.
- Nav station: **Clear** for the proximity and tracking warnings clears them for both players.

### Changed
- PDA chat keeps the last 100 messages (was 200).
- Speech bubbles are now one plate per speaker: their name on top, their last lines under it (older ones dimmer), a strip in the colour of how they're doing; "..." is its last line while they type.

### Fixed
- Client: looking into a container sometimes failed with "Unable to comply" (the host checked it against its own character).
- Client: no message when an action failed on the host, e.g. a repair missing parts.
- Client: every door or airlock opening or closing rebuilt all rooms of the ship. Now switched the game's way.
- The ship's hidden grid tiles are no longer synced (each side makes its own).
- Client: shops and vending machines restocked by themselves on the client too, leaving extra stock the host didn't have. Restock is the host's now.

## [0.40.2] - 2026-11-08

### Fixed
- Multiplayer window: after Host or Join it kept its old size and the **Report bug** row shifted.

## [0.40.1] - 2026-11-08

### Changed
- Item log: items sent and made in full, removed (with kind and place), asked for, and made by the client on its own.

## [0.40.0] - 2026-11-08

### Changed
- Client: your character's actions show at once, but their effects (damage, items, things breaking) come only from the host. A bashed wall breaks on both at the same moment, with no extra debris.
- Client: the game's "unable to" / "can't reach" messages for your character are hidden; the host's reason is shown when it can't carry out a direct order.

## [0.39.3] - 2026-11-08

### Changed
- Host log: a client action failing the host's check names what failed.

## [0.39.2] - 2026-11-08

### Fixed
- Client: task work (bash, repair, build) no longer pauses for a moment while the host's work manager retries.

## [0.39.1] - 2026-11-08

### Fixed
- Client: no "Unable to comply" in the character's log for a step the game retried another way.

## [0.39.0] - 2026-11-08

### Fixed
- Client: a direct order the host couldn't carry out is dropped on the client too.

## [0.38.1] - 2026-11-08

### Removed
- The temporary satiety log (0.35.11).

## [0.38.0] - 2026-11-08

### Added
- **Report bug:** links to Discord and GitHub at the bottom of the Multiplayer window.

## [0.37.5] - 2026-11-08

### Fixed
- The Multiplayer button's material was left behind each time the main menu opened again.

## [0.37.4] - 2026-11-08

### Fixed
- Client: a save that arrived incomplete was loaded anyway. Now it isn't, and on any failed save transfer the client says why and disconnects, so the host no longer waits for it with the game paused.

## [0.37.3] - 2026-11-08

### Changed
- Instance log: a message repeated more than 10 times in a minute is counted instead of written, with one summary line per minute.

## [0.37.2] - 2026-11-08

### Changed
- The mod's logs no longer write the full path of the game's data folder, which contains the Windows user name.

## [0.37.1] - 2026-11-08

### Changed
- A client connecting again from the same address takes its place at once, without waiting for the old connection to time out. Other connections while a client is connected are refused and logged.

## [0.37.0] - 2026-11-08

### Fixed
- The host could freeze while sending the save over a slow connection: sending now goes through a queue written by its own thread, and the save waits its turn instead of holding up the game.
- A connection is dropped when the other side stops taking data for 20 seconds, or sends a message far bigger than the mod ever sends.
- A save still being sent when its client leaves is no longer sent on to the next client.

## [0.36.4] - 2026-11-08

### Changed
- Messages nobody handles are logged, once each (the host didn't log them at all).
- The player name is trimmed when saved; an empty name isn't saved.

## [0.36.3] - 2026-11-08

### Changed
- If a patch fails to install (e.g. after a game update), the rest still install. **Host** and **Join** are then off, and the main menu says which parts failed.

## [0.36.2] - 2026-11-08

### Changed
- The player name is saved with the **Save** button (or on **Host** / **Join**), not on every key typed.

## [0.36.1] - 2026-11-07

### Fixed
- Client: after a death, the new body's name wasn't saved to the profile, and "Waiting for host..." vanished with **New body** clickable again. The host's repeated DIED for the same body (sent with every new body asked for) reset the death window; it's now ignored. (First fixed as 0.35.2 on the desktop copy; that fix was lost when the files were overwritten later.)

## [0.36.0] - 2026-11-07

### Changed
- Lighter logging. The per-instance log (`OstranautsMP_<n>.log`) is written to disk once a second instead of after every line (the whole Unity log goes through it); errors and crashes are still written at once. The memory line is every minute instead of every 10 seconds and shows only the managed heap (the other two figures always read 0 under Unity). Walking orders and doors opening or closing are no longer logged.
- Host: "Resume Task(s)" and "Cancel Task" from the client are no longer pre-checked for the log. They're menu wrappers the game itself doesn't check, so the log always said "Unable to comply" though the action went ahead.

### Removed
- The temporary vizor log (0.35.6) and the "Loose item" log (0.35.5): they did their job.

## [0.35.11] - 2026-11-07

### Added
- Temporary log (to be removed): with each client menu action, both sides log the character's satiety and hydration and their hidden level flags (`DcSatiety…`, `DcHydration…`), to find why the host refused "Eat (Not Hungry)".

## [0.35.10] - 2026-11-07

### Changed
- Host log: each client resync now names the items (kind, ID, where they are on the host and on the client), not just counts.

## [0.35.9] - 2026-11-07

### Fixed
- A player's character jettisoned into space (the game allows it for the unconscious, stunned or recovering, not just the dead) vanished on the host without dying and lived on on the client. Now they die the usual way first (DIED, the new-body window), the client drops the body too, and the death record (training for the new body) is kept on the host until the game is restarted.

## [0.35.8] - 2026-11-07

### Fixed
- An item leaving a hand or a slot for a place the mod doesn't sync (into a placeholder while it's installed, a trade screen) stayed where it was on the client: e.g. after the host installed something they were dragging, the client kept seeing them drag it. The host now tells the client it's gone from there; the client hides it until its next place arrives.

## [0.35.7] - 2026-11-07

### Fixed
- Client: a person the host sends whole (a new body, someone who came on board) had their conditions frozen for good, the way the game freezes everything it makes from saved data until a ship load ends. Nothing could change them on the client, not even the host's CONDS: the client's new body kept stale stats, and the vizor reset flag never cleared, so the vizor rebuilt every frame and logged "No Slotted PDAs found…" endlessly.
- New body: the old wrist PDA went straight back on the wrist (the wrist is a hold slot), so the new PDA still didn't fit. The old one now goes to the inventory after the new one is on.

## [0.35.6] - 2026-11-07

### Fixed
- New body: the PDA the host gives didn't go on the wrist when the random person already wore one (a locked PDA), so the client had no working PDA and the game said "No Slotted PDAs found…" in the log again and again. Whatever is on the wrist now comes off first (into the inventory); the log names it.

### Added
- Temporary client log: each vizor rebuild (first 40) with who's selected, the reset flag and the callers, to find what keeps rebuilding it.

## [0.35.5] - 2026-11-07

### Changed
- Item check: back to how 0.35.3 placed items (0.35.4's "on the floor" guess for stale-parent items could put items on the client's floor that the host doesn't show). Only the log line stays: each item with a stale parent is named once (`Loose item …`), to learn what they are.

## [0.35.3] - 2026-11-07

### Fixed
- Character names can't have `,` `;` `=` or `|` any more (character editor, mirror, new body, death window). A name is the person's ID, and the multiplayer messages split on these: such a name broke positions, conditions and the item check for that person.

## [0.35.2] - 2026-11-07

### Added
- Version check: if the other player has another version of the mod, both sides drop the connection and say which versions (in the middle of the screen, in the chat and in the Multiplayer window). Before, different versions played on and quietly broke.
- Connection lost in the game (the other player crashed, left or timed out): both sides see it in the middle of the screen and in the chat, and the game is paused on both.

## [0.35.1] - 2026-11-07

### Fixed
- Client: the work rate over a working person always read x0.0 (the client counted it itself and missed the tools in use). The host now sends its own label ("repair x1.4") with the progress bar.
- Closing a shop (or another game window that used to pause) no longer sets the pause back to what it was when the window opened, which could undo a pause the other player set meanwhile.
- PROFILE app: switching tabs or Edit / Save / Cancel no longer changes the layout in the middle of a frame (Unity's "Getting control … position" error, the page flickering).

## [0.35.0] - 2026-11-07

### Added
- **PROFILE** app on the PDA: portrait, name, skills, training progress bars and a **Biography**. Edit and save your biography there; it's kept with your multiplayer profile (`Bio` in `OstranautsMP_profile.json`). The **Me** / other-player tabs show either player; the biography is sent to the other player on connect and when it changes.
- Speech bubbles stack: a new line gets its own bubble at the bottom, the older ones float up and fade on their own timers, up to three per speaker.

### Changed
- In multiplayer, game windows that pause the game (shops, jobs, finance, transit, ship broker...) no longer pause it. Space and the pause while the client loads work as before.

### Fixed
- Client: the host's character (or anyone) vanished after walking onto a docked ship; changing clothes didn't help, reconnecting did. Positions now carry the ship, and the client moves the person onto it as on the host.

## [0.34.4] - 2026-11-07

### Added
- The client's character gets a working PDA on the wrist (like a new player's: a PDA with a random cartridge) when claimed or made as a new body, so the vizor (Power, Damage... on the PDA) works for the client. Crew members and new bodies come with a locked PDA, which the vizor doesn't take; it goes into their inventory. Nothing changes if they already have a working one.

## [0.34.3] - 2026-11-07

### Fixed
- Client: "No Slotted PDAs found. Vizor app requires…" repeated thousands of times (seen after a new body). The vizor's one-shot reset flag on the client's character was never cleared on the host, so every full condition update brought it back and the client's vizor reset again. The host now clears it once it's sent.
- Quick chat: the first letter was sometimes lost ("от так" for "вот так"). A field that gets the focus selects its text, and the next key replaced it; the caret now goes to the end instead.

## [0.34.2] - 2026-11-07

### Changed
- Character editor, **Skills** tab: text and check boxes 1.5 times bigger; clicking a skill's name ticks it too.
- The client's death window reads in the game's voice: "CLONE LINK LOST", a lost link to the clone, a new neural code at a station, and a warning about 20% neurodegeneration of cognition and skills.

## [0.34.1] - 2026-11-07

### Changed
- A new body (the first one too) comes as the profile made it: none of the game's random training for a new crew member; after a death, only 80% of the last body's.
- "In port" means a faction's working station (OKLG and the like): on it or docked with it. Abandoned stations (derelict or nobody's) don't count.

### Fixed
- Client: while dead, the client could select and order other people (the host ignored it, but they jerked about on the client). Now the client only watches.
- After a reconnect, **New body** in the pick window asked with the dead body's name and only got "name is taken". The host now opens the death window for it (with a free name).
- The dead body is also looked for among a ship's saved items, in case the game files it there.

## [0.34.0] - 2026-11-07

### Added
- **Death of the client's character.** The host decides it; the client's character no longer dies on the client by itself (except while apart, where it's only logged).
  - The body stays with its name, things and history; it's no longer marked as the profile's.
  - The client stays in the game watching (free camera, no orders) with a **Your character died** window: a name for the next body (the old one with II, III... by default) and a **New body** button. The button works while the host's ship is in port (docked with a station, or the host is on one).
  - The new body is made like the first one (looks and skills from the profile), with the name asked for, and gets 80% of the last body's training (Training: ...). Skills learned in the game are lost until trained again. The name must be free; if it's taken, the host suggests the next free one. The client's profile takes the new name.
  - A client that reconnects after its character died gets the same window.
- The very first body of a profile is made as before, anywhere.

### Not handled
- The host's own death (the game's game over, as before) and a death while apart (only logged).

## [0.33.3] - 2026-11-06

### Fixed
- New body: a profile whose character had died couldn't get a new body (the dead body still counted as "your character", here or on a ship the host left). Dead characters no longer count, and the client's **Choose your character** list no longer shows dead crew members.

## [0.33.2] - 2026-11-06

### Fixed
- New body: a profile could get a second body when its character was somewhere the host isn't (a station the host had left), because the host only looked at what it had loaded. The host now also looks at the ships it has unloaded and answers "your character <name> is on <place>".

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
