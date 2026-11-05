# VAST Lab Metaverse Networking Toolkit (`com.vastlab.vast-metaverse-networking`)

A reusable Unity package developed by VAST Lab providing multiplayer networking and 3D spatial voice chat infrastructure for metaverse and virtual twin experiences.

## Engine & Dependencies
- **Engine:** Unity 6 (6000.6+)
- **Networking Stack:** Unity Netcode for GameObjects (NGO) 2.13+, Unity Transport (UTP)
- **Services:** Unity Gaming Services (UGS Authentication, Lobby, Relay), Vivox Voice Chat (16.5+)
- **Companion Package:** `com.vastlab.vast-metaverse-tools`

## Major Features & Capabilities
- **Automated Matchmaking & Relays:** Manages UGS Lobby registration, heartbeat maintenance, and Relay allocation for direct host-client or peer-to-peer gameplay sessions without port forwarding.
- **Player Synchronization:** Synchronizes networked player transforms, animations, VRM avatars, and spatial nametags across clients.
- **3D Spatial Voice Chat:** Integrated Vivox voice service with configurable 3D positional audio, distance-based attenuation roll-off, and channel management.
- **Scene Object Synchronization:** Replicates interactable objects and state changes across the virtual environment.

## Installation
Add via Unity Package Manager using Git URL:
```
https://github.com/VAST-Lab/vast-metaverse-networking.git
```
