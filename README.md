# Monopoly Poker Mod Helper

This repository contains mods for `Monopoly Poker`.

Currently a WIP.

## Overview

`Monopoly Poker` is an Unity IL2CPP game on Steam, so mods should be built for `IL2CPP`. BepInEx IL2CPP is used in this project.
This repo is focused on reverse engineering game behavior, finding useful types through reflection, and automating helper actions.

## Requirements

- Visual Studio
- `.NET 6`
- `BepInEx IL2CPP`
- `dnSpy` (for inspecting game assemblies and objects)

## Setup

1. Open the solution in Visual Studio.
2. Ensure the project targets `.NET 6` and references the game assembly as needed.
3. Build the helper project.
4. Deploy the output into the BepInEx plugin folder.

## Useful References

- BepInEx IL2CPP: https://github.com/BepInEx/BepInEx
- Configuration Manager: https://github.com/BepInEx/BepInEx.ConfigurationManager/releases/tag/v18.4.1
- [UnityExplorer](https://github.com/sinai-dev/UnityExplorer) (for browsing game assets and types during runtime)
- **BepInEx Docs**: <https://docs.bepinex.dev/>
- **Harmony Docs**: <https://harmony.pardeike.net/>

Note UnityExplorer is archived, need to use a fork.