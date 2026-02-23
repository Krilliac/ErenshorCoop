# Erenshor Co-Op
Brings Co-Op to Erenshor.

Please see the changelog for updates.<br><br><br>

## Current Features
- Steam Lobby support
- Dedicated server support (standalone, no game client required)
- Player sync
- Enemy and NPC sync
- Chat (Including Whispers)
- Grouping
- Spell Effects
- Sims are fully synced
- HP and MP healing spells
- UI! Press escape in-game to see the UI (might be off-screen, please report)
- Boss adds
- A few additional spawns are synced
  - Malaroths
  - Chessboard
  - Siraethe wards
  - Treasure Guardians
- Buffs/Debuffs
- Shared group XP
- Summons
  - When connecting or hosting you are required to re-summon for other players to see it.
- Trading
  - Pick up an item from the inventory or bank and drop it outside the UI
  - The items get deleted if you disconnect
  - They should appear again after a zone change, if not let me know
  - Confirmation window (can be disabled in the settings)
- Player Markers
- Weather and day/night cycle sync
- Zone ownership transfer


## Known Bugs
- Other players have sim chat behaviour


## Mention Worthy
- Skin Colors are not correctly applied (game bug).
- Each player requires around ~20kb/s upload.
- The host will need around ~100kb/s upload per player.

## Building
The project references game DLLs from your Erenshor installation. By default it looks in `F:\steam\steamapps\common\Erenshor`. To override:

```
msbuild /p:ErenshorGamePath="D:\Games\Erenshor" ErenshorCoop.sln
```

Or create a `Directory.Build.props` file in the repo root:
```xml
<Project>
  <PropertyGroup>
    <ErenshorGamePath>D:\Games\Erenshor</ErenshorGamePath>
  </PropertyGroup>
</Project>
```

## Usage
When in-game, use the UI in the escape menu.


## FAQ

    Q: How much bandwidth do I require to host?
    A: You will need around ~100-150kb/s upload for each player connected to you.

    Q: Can there be a standalone server?
    A: Yes! A dedicated server is now available. See the DedicatedServer project for details.

    Q: How can I report issues, bugs or make a suggestion?
    A: Please either join the Erenshor discord and post your issues in the appropriate mod channel, or create an issue on Github.

    Q: Is this mod compatible with other mods?
    A: This is largely untested, and probably no.