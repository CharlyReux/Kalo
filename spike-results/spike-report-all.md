# Kalo Spike 0 report: all

- Started: 2026-10-28 19:55:24
- Command: `kalo-spike all`
- HREK: `D:\SteamLibrary\steamapps\common\HREK` (build 2023.07.17.176677.1-QFE1)
- Workspace: `D:\charl\Documents\Perso\Halo\Kalo\.kalo\workspaces\spike`

## Environment

- Windows: Microsoft Windows NT 6.2.9200.0, 64-bit
- This process: 64-bit
- .NET Framework: 4.8.1 (release 533509)
- [x] Running as a 64-bit process

## HREK

- Folder: D:\SteamLibrary\steamapps\common\HREK
- Build: 2023.07.17.176677.1-QFE1
- tags/: 39,43 GB in 62 017 files
- data/: 0,03 GB in 707 files

## MCC

- Folder: D:\SteamLibrary\steamapps\common\Halo The Master Chief Collection
- Build: 2025.08.16.178512.1-Release   / Release

## Disk

- Free space on D:\ (workspace drive): 76,41 GB

## Workspace

- To copy: 39,92 GB in 63 150 files
- Free space: 76,41 GB
- Linked bat/: junction to HREK
- Linked bin/: junction to HREK
- Copied data/: 0,5 s
- Linked data_cht/: junction to HREK
- Linked data_de/: junction to HREK
- Linked data_fr/: junction to HREK
- Linked data_it/: junction to HREK
- Linked data_jpn/: junction to HREK
- Linked data_kor/: junction to HREK
- Linked data_mx/: junction to HREK
- Linked data_pt/: junction to HREK
- Linked data_sp/: junction to HREK
- Linked fmod/: junction to HREK
- Copied maps/: 0,8 s
- Copied prefs/: 0,0 s
- Created empty reports/: ok
- Copied tags/: 1086,0 s
- Copied user/: 0,2 s
- Total time: 1088,2 s
- [x] Workspace created

## ManagedBlam

- Start (TagsOnly): 93 ms
- Tag root: d:\charl\documents\perso\halo\kalo\.kalo\workspaces\spike\tags
- Engine build: untracked version
- Tag groups known: 235
- [x] Tag root is inside the workspace

## Field dump: assault_rifle.weapon

- Load: 59 ms
- Fields walked: 459 in 27 ms (blocks capped at 64 elements)
- Value fields (numbers, enums, flags, references): 312
- Fields with units: 58
- Fields with a description: 150
- Fields with a display name different from the raw name: 3
- Dump: assault_rifle.fields.tsv
- [x] Walked the whole tag

## Edit and save

- Field: `Block:magazines[0]/ShortInteger:rounds loaded maximum` (ShortInteger)
- Value before: 32
- [x] SelectField(path) finds the same field
- SaveAs: 41 ms
- [x] Saved tag is in the workspace (objects\weapons\rifle\assault_rifle\assault_rifle_kalo_spike.weapon)
- [x] Nothing was written to HREK's tags folder
- Value after reload: 36
- [x] Reloaded value is 36
- [x] The workspace's original assault_rifle.weapon is unchanged

## Load speed

- Weapon tags loaded and walked: 32 (13 090 fields)
- Per tag: median 11 ms, max 14 ms
- Total: 361 ms
- [x] All sampled weapon tags loaded
- Stop: 23 ms

## tool.exe

- Command: tool.exe (no arguments)
- Exit code: 1
- Run time: 1221 ms
- Time to first output: 1149 ms
- Output lines: 305
- Full output: tool-usage.log
- [x] tool.exe ran and its output was captured

## HREK safety check

- Files watched in HREK: 334
- [x] No file in the HREK folder was created, changed or deleted

## Result

All checks passed.
