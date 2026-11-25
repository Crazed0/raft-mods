# Raft Mods

A pack of 14 mods for [Raft](https://raft-game.com/), built with [Raft Mod Loader](https://www.raftmodding.com/) (C#, Unity, Harmony). Each mod is meant to look and feel like part of the game, and they are played in multiplayer by a small group of friends.

## Highlights

- **Mod Sync**: keeps everyone's mods folder in sync with a pack hosted on the web. It downloads changed mods (verified with SHA-256) and **hot-reloads them while the game is running**. No restart is needed, not even when Mod Sync updates itself. If a new version fails to start, it rolls back to the previous one.
- **Hardcore Survival**: sleep, fatigue, raw-food sickness, wetness, and body temperature, with cold shivers and overheating fever. Its HUD rows are cloned from the game's own stat bars.
- **Fish Fight**: catching a fish becomes a fight on a tight line, with a native-style UI.
- **Skills**: six skills that level up as you play, saved per world, shown in a leather-book panel.
- **Inspecionar**: when you look at a grill, purifier, cooking pot, crop plot or tank, a parchment window shows its fuel, progress and contents.

## All mods

| Mod | What it does |
|---|---|
| **Breakable Big Palms** 1.0.2 | The big island palms become choppable trees (8 axe hits, 3x the drops of a normal palm). |
| **Calm Fishing** 1.7.1 | While anchored, floating items keep coming on a natural sea current that carries them past the raft (at an island they wash up on its coast), with an item limit (anti-lag). |
| **Fish Fight** 1.4.0 | Catching a fish is a fight on a tight line instead of one click. |
| **Hardcore Survival** 2.4.4 | Harder survival, per world: sleep in a bed, sickness from raw food, wetness, and body temperature with cold and fever. |
| **Inspecionar** 1.2.0 | Look at a grill, purifier, smelter, cooking pot, juicer, crop plot or tank : a small parchment window shows fuel, battery, water and how far along each item is. |
| **Couro e Papel** 1.6.0 | Reskin of the game menus (crafting, inventory, hotbar, research table, buttons) in leather and paper, matching the BestCode panels. |
| **Diario de Bordo** 1.2.0 | Diario de bordo no T: ilhas, peixes, comida, viagem e registo por mundo. |
| **Mod Sync** 2.1.3 | Keeps the mods folder equal to the pack at bestcode.pt/raft: downloads updated mods (SHA-256 verified) and hot-reloads them in game, no restart. |
| **Quick Autosave** 1.3.1 | Autosaves every 1 minute instead of 10. |
| **Raft Life** 1.1.0 | Small natural touches. |
| **Shark Attack Delayed** 1.2.2 | Modifies how sharks behave. |
| **Skills** 1.2.0 | Six skills that level up as you play, saved per world: Pescador (fish pull less), Recolha (hook goes further and grabs faster), Lenhador (chance of double wood), Cacador (more damage to animals), Mergulhador (oxygen lasts longer), Agricultor (chance of an extra crop). |
| **Stackable Chests** 1.1.0 | Place chests on top of other chests, exactly aligned. |
| **Tree Harvest Fix** 1.0.0 | Fixes the NullReferenceException in HarvestableTree when another player cuts trees added by More Bananas / NoMoreUselessPalms. |

## Layout

- `src/<mod>/`: the C# source and `modinfo.json` for each mod.
- `src/bcui_kit.cs.txt`: a shared UI kit (leather, paper and stitch styling) that each mod copies into its own namespace.
- `build.ps1` / `update.ps1` / `publish.ps1`: compile every mod into `.rmod` files and publish the pack.
- `site/`: the download page for the pack.
- `GUIA_MODS.md`: internal development guide, in Portuguese.
