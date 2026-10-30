# Kalo

A modern modding workbench for Halo: Reach (MCC), built on top of the official Halo Reach Editing Kit (HREK).

- Design doc: https://claude.ai/artifact/Fsh4bt9uP1xP8H7CMdWGQ8
- UI mockup: https://claude.ai/artifact/7PetLn2NLhZ1LxxBweqAe6

Status: **working draft** of the app: tag editor with blocks and references, 3D map editor, gametype script
editor, mod projects, Play with hot reload. The spikes before it were
throwaway command-line probes:

- Spike 0 (passed): can Kalo drive ManagedBlam (tags) and tool.exe (builds) from its own process, inside a
  Kalo workspace, without ever writing to the HREK or MCC folders?
- Spike 1 (done): can Kalo edit maps live? Hot reload of saved tags in reach_tag_test works; RemoteCommand,
  in-process engine rendering and Forge variant editing do not (yet). See [Spike 1](#spike-1-live-3d-editing).

## Running Kalo

```powershell
cd D:\charl\Documents\Perso\Halo\Kalo
dotnet build Kalo.sln
src\Kalo.App\bin\Debug\net8.0\kalo.exe
```

- Start page: create a mod (a name is enough), then New map / New gametype / Tweak a weapon.
- Left, "My mod": only what your mod contains, grouped as Maps, Gametypes, Weapons & objects, Sounds,
  UI & text, Effects & visuals. The + of a group creates (maps, gametypes) or opens the game files to pick
  something to change; right-click a file to compare it with vanilla or take it out of the mod.
  New map: a Reach level either empty (spawn points only) or fully copied, saved in levels\mods\<mod>\<map>.
  New gametype: a copy of a template (simple commented starter, Slayer, CTF, KotH, Oddball, Juggernaut...)
  in data\multiplayer\megalo\mods\<mod>\ (in Kalo Script by default). Play defaults to Slayer.
- Left, "All game files" (advanced): every tag of Halo Reach (Ctrl+P searches). Open one and save it to
  add it to your mod.
- Play (F5), top right: plays the chosen map with the chosen gametype. Both follow the tab you work in
  (a map tab picks the map, a gametype tab the gametype); unsaved changes are saved first and your gametype
  is compiled and loaded into the game.
- Middle: the tag editor. "Labeled" shows friendly names and hides engine internals, "Raw fields" shows
  the engine's names and types. Type a value and press Enter: Kalo checks it with the engine right away.
  Save (Ctrl+S) writes the tag into the shared workspace, never into HREK.
- While the game runs, every tag you save is reloaded within a second (change the assault rifle's magazine
  size and look at its ammo counter). Object placements need a restart: press Play again.
- The Changes panel (second icon on the left) lists every file that differs from vanilla HREK and puts any
  of them back. "Revert to vanilla" in the editor does the same for the open tag.

- Blocks: right-click a block row (or use the inspector) to add, insert, duplicate or delete elements.
  References: select one and pick a tag of an allowed type in the inspector, or open the referenced tag.
- Maps: double-click a .scenario (or search for one). The map view shows the level (read from the
  structure_bsp tags, cached in .kalo\cache\geometry) and every object placement with its model (or a box).
  Shading, top right of the view (Z cycles, Shift+Z wireframe; W on AZERTY): Wireframe, Solid, or Textured
  (each shader's color map with its tiling, terrain layers blended by their blend map, water tinted, glass,
  decals and lights blended like in the game). "Edges" overlays triangle edges.
  Right-drag looks around, WASD/QE fly (ZQSD/AE on AZERTY; Shift faster), click selects, drag moves (Shift: up/down,
  Ctrl: snap to 0.25), R turns 15°, Ctrl+C / Ctrl+X / Ctrl+V copy, cut and paste, Ctrl+D duplicates, Delete
  removes, F frames, Home frames the play space. Every key can be changed in Settings (gear, top right).
  New objects (Add, paste, prefabs, the Objects tab) go on the surface in front of the camera.
- Object list (left of the map): the map's Sapien editor folders, then the objects in no folder grouped by kind.
  Green = new, amber = changed compared with the map it came from (Reach's own file, or the level a new map was
  copied from). Double-click goes to the object. Right-click: copy, cut, paste, duplicate, delete, new folder
  (the selection goes in it), move to folder, rename/delete folder, create prefab.
- Inspector: position, rotation, scale and every other property of the selected object (flags, team, variant,
  colors...), each edited directly in the map.
- Block tools (left edge of the map view): Floor, Block, Wall, Ramp, Stairs, Column, Bridge, Cover place Forge
  pieces in front of the camera (snapped to a half-unit grid). Paint: pick any of ~230 Reach level materials;
  Kalo saves a painted copy of each piece in your mod (levels\mods\<mod>\blocks\...) and swaps the object to it.
  Blocks are objects, so they are instant and playable; baking them into real level geometry comes later.
- Objects tab (bottom, next to Output): every placeable object by category (Forge blocks, weapons, vehicles,
  spawning...), searchable, with a 3D preview you can turn. Double-click, Add to map, or drag it onto the map.
- Prefabs tab: groups of objects saved with the mod (projects\<mod>\prefabs). Place one in front of the camera.
- New map: Barebone (a platform with 4 spawn points on HREK's open test floor, made a multiplayer map),
  Empty Reach level (spawns only) or Remix (full copy).
- Gametypes (third icon on the left): the Megalo scripts from data\multiplayer\megalo. F7 compiles with
  HREK's own compiler: errors with line numbers, the gametype budget, declared variables, and a searchable list
  of every action and condition with its description (double-click inserts it). Autocomplete pops up after
  "action " and "condition " and while typing (Ctrl+Space forces it): actions, conditions, keywords, the engine's
  value lists (weapons, vehicles, object types, sounds...) and the script's own variables. "Test in game" compiles,
  deploys the engine to maps\megalo\kalo_<name>.mglo in the workspace (as MegaloEdit does for a devkit) and
  starts the map picked at the top with `load_binary_game_engine kalo_<name>` in bonobo_init.txt.
- Kalo Script (`.kscript`): gametypes in a modern syntax close to ReachVariantTool's, compiled through HREK's
  own Megalo compiler. New gametypes are written in it by default (untick "Write it in Kalo Script" to get Megalo
  text), and "To Kalo Script" converts any Megalo script (a new file in your mod; the original stays). "Megalo"
  shows the text it turns into; compiler errors point at the Kalo Script line. Every Bungie script that compiles
  converts and compiles back to the same triggers, conditions and actions (`kalo-cli kscript-roundtrip`).

  ```
  -- title: Streaks
  megalo [[            -- Megalo declarations (options, traits, map objects...) stay as they are
  game_options
  	ranged_option option_kill_points
  		"Kill Points"
  		"Points per kill"
  		1 -10 10
  	end
  end
  ]]
  declare player.number streak = 0          -- declare [networked|local] global/player/team/object.type name = value

  for each player do                         -- trigger player   (also: team, object, random player, a map object)
    if player_died(current_player, enemy) then
      temp player killer = none              -- temporary
      player_death_get_killing_player(current_player, killer)
      killer.score += option_kill_points     -- action set killer.score += ...
      if killer.streak == 4 and not killer.team == none then
        hud_post_message(player killer, none, "Spree!")   -- several-word arguments are fine
      elseif killer.streak > 10 then
        killer.streak = 0
      else
        killer.streak += 1
      end
    end
  end
  on init do ... end                         -- trigger initialization (also local, host_migration, pregame)
  do ... end                                 -- trigger general (nested: action for_each general)
  inline ... end                             -- Megalo's begin ... end: a block that uses no trigger
  for each object with label "koth_hill" do ... end   -- declares the map object for you
  ```
  `a.f(b)` is the same as `f(a, b)`; `alias name = path` renames; conditions join left to right with no precedence
  (`a or b and c` is `(a or b) and c`), as in Megalo.
- Export .bin (script toolbar): writes the gametype as an MCC game type file in Kalo\exports\gametypes (BLF
  chdr + mpvr with its SHA-1, built from the compiler's variant). A `-- title: Name` line names it. Copy it into
  `%USERPROFILE%\AppData\LocalLow\MCC\LocalFiles\<id>\HaloReach\GameType` to pick it in Custom Games.
  Experimental: not yet tried in MCC.
- Split editors: the buttons on the right of every tab strip split side by side, top and bottom, or put the second
  editor in its own window (code on another screen); swap sides, close the split. Right-click a tab (or drag it onto
  the other side) to move it. The inspector, shortcuts and Save follow the side you last clicked; files you open
  go to that side.
- Map editing: Undo/Redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, toolbar arrows) for every map edit; Shift+click adds or
  removes objects from the selection; Ctrl+C/X/V/D, Delete, R, F work anywhere in the map tab (not while typing).
  Objects outside the level geometry are marked "outside map" in red with a banner: Reach removes them in game.
  "Start locations (first spawn)" in the object list are where players first appear (respawn points are only used
  after dying). Scale is ignored by Reach in multiplayer games (the inspector says so).
- Mod projects (fourth icon): a project is a small folder in Kalo\projects holding only the files your mod
  changes. Every save is recorded in the active project; switching projects puts the shared workspace back to
  vanilla and loads the other project's files; Export zips a project into Kalo\exports; "Import mod…" adds such a
  zip back as a project. "Diff" in the Changes panel (or "Compare" in the tag editor) lists every field that
  differs from vanilla HREK, or the changed lines of a script.

Self-tests, used through the runner: `kalo.exe --tour .kalo\shots\tour` walks through blocks, references, the
map view (handles, multi-select, drop), the script editor (autocomplete) and projects (diff, import); nothing is
saved. `--map-play-test`, `--megalo-play-test` and `--mod-play-test` (new mod → new empty map + new gametype → Play)
check the game side in reach_tag_test. `kalo.exe --self-test .kalo\shots\selftest` opens the assault rifle,
sets the magazine size, saves, starts the game, changes it again while the game runs, waits for the game
to reload it, stops the game, reverts, and checks HREK was not touched. It prints `SELFTEST PASS` or the
failed checks and saves screenshots of the Kalo window.

## Layout

```
Kalo/
  Kalo.sln
  Directory.Build.props        HREK location (HrekRoot), shared build settings
  src/Kalo.App/                the Kalo window (Avalonia, net8.0)
  src/Kalo.Core/               workspace, change tracking, Blam Host client, play test (net8.0)
  src/Kalo.Protocol/           messages between the app and the Blam Host (netstandard2.0)
  src/Kalo.BlamHost/           background process that runs ManagedBlam (net48, x64; HREK's DLLs need it)
  src/Kalo.Cli/                command-line checks (host-test, changes, play-test)
  src/Kalo.Spike0/             Spike 0 probe (net48, x64)
  src/Kalo.Spike1/             Spike 1 probe (net48, x64), shares a few files with Spike 0
  spike-results/               reports written by the probes
  .kalo/shared/                the shared workspace used by every project (git-ignored, ~40 GB)
```

## One-time setup

Open **PowerShell** and run:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install Git.Git
```

Close and reopen PowerShell afterwards so `dotnet` and `git` are on the PATH. Check with:

```powershell
dotnet --version
git --version
```

Windows 10/11 already include the .NET Framework 4.8 runtime that ManagedBlam needs. The first build downloads
the 4.8 reference assemblies automatically.

Optional editor: [Visual Studio Code](https://code.visualstudio.com) with the **C# Dev Kit** extension, or
Visual Studio 2022 Community / JetBrains Rider later, when the real UI work starts.

Then create the local Git repository:

```powershell
cd D:\charl\Documents\Perso\Halo\Kalo
git init
git add .
git commit -m "Spike 0: ManagedBlam and tool.exe probe"
```

If your HREK is not at `D:\SteamLibrary\steamapps\common\HREK`, set it once:

```powershell
setx KALO_HREK "E:\path\to\HREK"
```

## Running Spike 0

From the `Kalo` folder:

```powershell
# 1. Read-only checks: HREK and MCC builds, size of tags/ and data/, free disk space
dotnet run --project src\Kalo.Spike0 -- env

# 2. Everything: env, create the workspace, ManagedBlam test, tool.exe test
dotnet run --project src\Kalo.Spike0 -- all
```

`all` asks before copying anything. The copy is HREK's `tags/` and `data/` folders (the size is shown first),
so expect several GB and a few minutes. Re-running reuses the existing workspace.

Individual steps: `workspace`, `tags`, `tool`. Options: `--hrek <dir>`, `--workspace <dir>`, `--results <dir>`, `--yes`.

### What each step does

| Step | What happens | Writes to |
| --- | --- | --- |
| `env` | Reads build numbers, folder sizes, free space | `spike-results/` only |
| `workspace` | Copies `tags/ data/ maps/ prefs/ user/` and root files (not `HREK.7z`); links `bin/ fmod/ bat/ data_xx/` with NTFS junctions; creates an empty `reports/` | `.kalo/workspaces/spike/` |
| `tags` | Starts ManagedBlam on the workspace (tags only), dumps every field of `assault_rifle.weapon`, changes its magazine size by +4, saves it as a **new** tag `assault_rifle_kalo_spike.weapon`, reloads and checks it, then times loading 40 weapon tags | the workspace only |
| `tool` | Runs the workspace's `tool.exe` with no arguments (it just prints its command list) and captures the output | `spike-results/` only |

### Safety

- The probe refuses to write anywhere inside the HREK or MCC folders.
- Before any save it checks that ManagedBlam's tag root is the workspace, not HREK.
- `tags`, `tool` and `all` take a snapshot of every HREK file the workspace links to (plus HREK's root files)
  before and after, and fail the report if anything changed.

### After running

Reports land in `spike-results/`:

- `spike-report-<command>.md` with every measurement and check
- `assault_rifle.fields.tsv` with all fields of the weapon tag (path, type, display name, units, description, value)
- `tool-usage.log` with tool.exe's output

Nothing to send: the folder is shared with Claude, so just say it has run.

### If something goes wrong

| Symptom | Likely cause |
| --- | --- |
| `BadImageFormatException` | The process is not 64-bit. Run it with `dotnet run` as shown, not from a 32-bit shell. |
| `Could not load file or assembly 'ManagedBlam'` | HREK path is wrong. Check `KALO_HREK` or pass `--hrek`. |
| ManagedBlam crash or assertion message | The report keeps the message and engine file and line; this is exactly what Spike 0 is meant to find. |
| A window from HREK pops up | Note what it says; close it. The report still gets written. |

## The Kalo runner (lets Claude build and test on this PC)

Claude can write files in this folder but cannot run programs. The runner closes that gap: start it once,
leave the window open, and Claude can build Kalo, run tests and take screenshots on its own.

```powershell
cd D:\charl\Documents\Perso\Halo\Kalo
powershell -ExecutionPolicy Bypass -File tools\kalo-runner.ps1
```

- Claude drops job files in `.kalo\jobs\inbox\`; results go to `.kalo\jobs\done\<job>\` (logs, outputs, screenshots).
- Only these steps exist: `dotnet build|run|test|restore|clean`, Kalo's own built `.exe` files, the game tools
  inside `.kalo\shared` (reach_tag_test, tool, sapien...), screenshots, waits, closing those tools,
  and `git status|add|commit|log|diff`. Every path must stay inside this Kalo folder; HREK and MCC are never touched.
- While it runs, Windows will not go to sleep. Game windows may open and close on their own.
- Stop it any time with Ctrl+C or by closing the window. Read `tools\kalo-runner.ps1` to see exactly what it does.

## Spike 1: live 3D editing

From the `Kalo` folder:

```powershell
dotnet run --project src\Kalo.Spike1 -- all
```

`all` runs four steps, each in its own process so a crash in one does not hide the others:

| Step | What happens | Writes to |
| --- | --- | --- |
| `shared` | Renames the Spike 0 workspace to `.kalo\shared` (no copy, instant), removes Spike 0's test tag, records a baseline index of every file in `tags/` and `data/` | `.kalo\shared` |
| `forge` | Reads the Forge map variants MCC ships (`haloreach\map_variants\*.mvar`, read-only), loads them with ManagedBlam, writes them back to bytes and compares, exports them to XML | `spike-results\forge\` |
| `live` | Starts `reach_tag_test.exe` on Sword Base, connects to it, runs a script command, places an assault rifle, then changes the rifle's magazine size live in the game (99, then 46 to 50) without saving the tag. **Asks you** whether you saw the change, then offers to close the game | `.kalo\shared` (a temporary `bonobo_init.txt`) |
| `engine` | Starts the engine inside Kalo's own process, loads Sword Base, runs about 900 frames and saves what the engine's viewport rendered as `engine-frame.png` | `spike-results\` |

Expect a few minutes: loading a map straight from tags is slower than from a built `.map`. A game window
opens during `live`; the `engine` step may open one too. Pick another map with
`--map levels\multi\forge_halo\forge_halo`, and allow more time with `--timeout 600`.

Each step checks HREK is untouched and lists which files in the shared workspace changed
(`spike-results\workspace-changes.txt`): that list is what a project's overlay would hold.

Single steps: `shared`, `changes`, `forge`, `live`, `engine`. Reports: `spike-results\spike-report-s1-*.md`.
