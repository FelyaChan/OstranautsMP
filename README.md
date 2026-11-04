# Ostranauts Multiplayer Mod (Direct IP)

A client-server multiplayer mod for **Ostranauts** utilizing an authoritative listen server model. One player acts as the Host (Server), and another connects as the Client via a direct IP address.

## How it works
* **Authoritative Host:** The host manages game logic, simulation, and world state.
* **Direct Connect:** Connection is established via TCP/IP directly between players.

---

## How to Play

### As the Host:
1. Launch the game and click the **Multiplayer** button in the main menu.
2. Enter your chosen Player Name, set the port to `7777` (default), and click **Host**.
3. Close the multiplayer window after hosting.
4. **Load your save** from the main menu (**Note:** The mod currently *does not work with creating a new game*).
5. Ensure you have **at least one additional crew member** in your squad for the client to control.
6. Open port **7777** (TCP) on your router (Port Forwarding), or use virtual network software like **Radmin VPN** / **Hamachi** so your friend can reach your PC.
7. Share your IP address with your friend.

### As the Client:
1. Launch the game and click the **Multiplayer** button in the main menu.
2. Enter your chosen Player Name.
3. Enter the Host's IP address and port (`7777`).
4. Click **Join** to connect to the session and wait for the host to finish loading.
5. Once connected, **select the available crew member at the top of your screen**. This character will permanently become yours.

---

## Installation Guide

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
