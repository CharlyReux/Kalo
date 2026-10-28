# Kalo

A modern modding workbench for Halo: Reach (MCC), built on top of the official Halo Reach Editing Kit (HREK).

- Design doc: https://claude.ai/artifact/Fsh4bt9uP1xP8H7CMdWGQ8
- UI mockup: https://claude.ai/artifact/7PetLn2NLhZ1LxxBweqAe6

Status: **Spike 0**. A throwaway command-line probe that answers one question before any UI work:
can Kalo drive ManagedBlam (tags) and tool.exe (builds) from its own process, inside a Kalo workspace,
without ever writing to the HREK or MCC folders?

## Layout

```
Kalo/
  Kalo.sln
  Directory.Build.props        HREK location (HrekRoot), shared build settings
  src/Kalo.Spike0/             the probe (net48, x64)
  spike-results/               reports written by the probe (created on first run)
  .kalo/workspaces/spike/      the test workspace (git-ignored, several GB)
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
