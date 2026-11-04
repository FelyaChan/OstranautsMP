# Ostranauts Multiplayer Mod (Direct IP)

A client-server multiplayer mod for **Ostranauts** utilizing an authoritative listen server model. One player acts as the Host (Server), and another connects as the Client via a direct IP address.

## How it works
* **Authoritative Host:** The host manages game logic, simulation, and world state.
* **Direct Connect:** Connection is established via TCP/IP directly between players.

## How to Play

### As the Host:
1. Launch the game and start your session.
2. Open port **7777** (TCP) on your router, or use virtual network software like **Radmin VPN** / **Hamachi**.
3. Share your IP address with your friend.

### As the Client:
1. Launch the game.
2. Enter the Host's IP address and port (`7777`).
3. Click **Join** to connect to the session.
