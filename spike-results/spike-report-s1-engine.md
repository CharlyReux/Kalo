# Kalo Spike 1 report: engine

- Started: 2026-10-28 20:51:30
- Command: `kalo-spike1 engine --hrek D:\SteamLibrary\steamapps\common\HREK --workspace D:\charl\Documents\Perso\Halo\Kalo\.kalo\shared --results D:\charl\Documents\Perso\Halo\Kalo\spike-results --map levels\multi\20_sword_slayer\20_sword_slayer --timeout 300`
- HREK: `D:\SteamLibrary\steamapps\common\HREK` (build 2023.07.17.176677.1-QFE1)
- Shared workspace: `D:\charl\Documents\Perso\Halo\Kalo\.kalo\shared`

## Engine inside Kalo's process

- [x] Map exists in the workspace: levels\multi\20_sword_slayer\20_sword_slayer

**FAILED:** ManagedBlam Halt: system_exit_application(_application_exit_fatal_error) (c:\mcc\qfe1\reach\shared\engine\source\omaha\managedblam\managedblam_shell.cpp:60)

**FAILED:** ManagedBlam would not start at GameEditor level: ManagedBlamCrashException: system_exit_application(_application_exit_fatal_error)

## Engine loading messages

- Messages: 0

## HREK safety check

- Files watched in HREK: 334
- [x] No file in the HREK folder was created, changed or deleted

## Workspace changes since baseline

- Scan time: 2276 ms (baseline load 97 ms, 62 724 files)
- Files that differ: 0

## Result

2 check(s) failed.
