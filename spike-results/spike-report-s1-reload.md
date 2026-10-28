# Kalo Spike 1 report: reload

- Started: 2026-10-28 21:02:23
- Command: `kalo-spike1 reload --yes --timeout 400`
- HREK: `D:\SteamLibrary\steamapps\common\HREK` (build 2023.07.17.176677.1-QFE1)
- Shared workspace: `D:\charl\Documents\Perso\Halo\Kalo\.kalo\shared`

## Tag hot reload in reach_tag_test

- [x] Map exists in the workspace: levels\multi\20_sword_slayer\20_sword_slayer
- reach_tag_test started: pid 4744
- Map loaded: after 28,2 s
- Log folder: D:\charl\Documents\Perso\Halo\Kalo\.kalo\shared\reports\reports_00
- Screenshot after load: reload-1-loaded.png

## Ports the game listens on

- Count: 2
    - UDP 0.0.0.0:1000
    - UDP 0.0.0.0:1001
- ManagedBlam start (TagsOnly): 85 ms
- Initialized to TagsOnly: True

## RemoteCommand on the game's ports

- Connect("127.0.0.1"): no (0 ms)
- Connect("localhost"): no (0 ms)

## Change the tag on disk

- Magazine before: 32
- [x] assault_rifle.weapon saved with magazine 77 (file timestamp changed)
- New log lines in 45 s: 35
    debug.txt: 10.28.26 21:02:57.917 0001007 g000002 f0000001  WARNING player_rewards:persistence: User timezone changed from 0 to -3600.
    debug_full.txt: 10.28.26 21:02:57.222 0000991 g000000 f0000000  message tags:xsync: reloaded objects\weapons\rifle\assault_rifle\assault_rifle.weapon...
    debug_full.txt: 10.28.26 21:02:57.917 0001007 g000002 f0000001  WARNING player_rewards:persistence: User timezone changed from 0 to -3600.
    tag_debug.txt: 10.28.26 21:02:57.222 0000991 g000000 f0000000  message tags:xsync: reloaded objects\weapons\rifle\assault_rifle\assault_rifle.weapon...
- [x] The game logged a reaction to the changed tag
- Screenshot after the change: reload-2-after-save.png
- ManagedBlam stop: 26 ms

## Clean up

- [x] Original assault_rifle.weapon restored (content and timestamp)
- reach_tag_test: closed

## HREK safety check

- Files watched in HREK: 334
- [x] No file in the HREK folder was created, changed or deleted

## Workspace changes since baseline

- Scan time: 2350 ms (baseline load 82 ms, 62 724 files)
- Files that differ: 0

## Result

All checks passed.
