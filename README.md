# Kalo

A modding workbench for **Halo: Reach** (Master Chief Collection), built on the official Halo: Reach Editing Kit (HREK).

Kalo puts the editing kit behind one friendly window: make a mod, change weapons, build or remix maps, write
gametypes and campaign missions, and try every change in the game in a few seconds.

> **Status:** work in progress. Windows only. Kalo never writes to your HREK or MCC folders: it works in its own
> copy of the kit's files.

## What you can do

**Multiplayer**
- New maps from a bare platform, an empty Reach level or a full copy of one, or change Reach's own maps.
- A 3D map editor: fly around, place, move and paint objects, build with Forge-style blocks, folders and prefabs.
- Gametypes written in Kalo Script (readable, ReachVariantTool-like) or raw Megalo, from templates such as Slayer,
  CTF, SWAT, Snipers, Fiesta and Gun Game. Loadouts, traits and "when a player enters this zone" actions.
- Export a map as a Forge variant, and check whether a mod can be played in MCC without mods.

**Campaign and Firefight**
- Copy a mission (or start an empty one) and change its squads, teams, patrol routes, vehicles and trigger volumes.
- Edit the mission scripts, with a script reference built in.
- Camera points for cutscenes: set them from the view, look through them, preview them in the game.
- Choose the player's starting weapons, grenades and armor ability.

**Weapons and objects**
- Change a weapon's damage, rate of fire, magazine and more in plain words, with "hits to kill a Spartan".
- Make a new weapon from an existing one (with its own bullet and damage), repaint it or put your own picture on it.

**Testing**
- **Play (F5)** opens your map and gametype in the editing kit's test game; saved changes reload while it runs.
- **Play here** drops you into the map where the camera is. **Clean screen** hides the engine's debug text.
- **Play in MCC** builds a real `.map` and installs it as a local MCC mod (start MCC with mods, anti-cheat off).

Help › Recipes walks through common tasks step by step.

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (and the .NET Framework 4.8 developer pack, if the build asks for it)
- Halo: Reach Mod Tools (HREK) from Steam
- Halo: The Master Chief Collection, for Play in MCC

## Build and run

```powershell
git clone git@github.com:CharlyReux/Kalo.git
cd Kalo
dotnet build Kalo.sln -c Debug
src\Kalo.App\bin\Debug\net8.0\kalo.exe
```

Kalo looks for HREK in `D:\SteamLibrary\steamapps\common\HREK`, both to build (it links the kit's ManagedBlam)
and to run. If yours is elsewhere, set the `KALO_HREK` environment variable before building, for example
`setx KALO_HREK "E:\SteamLibrary\steamapps\common\HREK"` (then open a new terminal). The first start copies the kit's files into Kalo's
workspace (`.kalo\`, several GB) and indexes them.

Then, on the start page: create a mod (a name is enough), and pick **New map**, **Mod a campaign mission**,
**New gametype** or **Tweak a weapon**.

## Project layout

| Folder | What it is |
| --- | --- |
| `src/Kalo.App` | The desktop app (Avalonia, .NET 8) |
| `src/Kalo.Core` | Workspace, mod projects, scripting languages, geometry, MCC mod files |
| `src/Kalo.BlamHost` | A .NET Framework process that talks to the editing kit's ManagedBlam |
| `src/Kalo.Protocol` | Messages between the app and the Blam host |
| `src/Kalo.Cli` | Command-line tools for tests and probes |
| `src/Kalo.Spike0`, `src/Kalo.Spike1` | Early experiments, kept for reference |

More detail on every panel, the experiments and the test tour: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Credits

Halo and Halo: Reach are trademarks of Microsoft. Kalo is a fan-made tool, not affiliated with or endorsed by
Microsoft, 343 Industries, Halo Studios or Bungie. It needs your own copy of the editing kit and the game.
