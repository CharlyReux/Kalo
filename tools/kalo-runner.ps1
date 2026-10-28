<#
.SYNOPSIS
    Kalo runner: lets Claude build and run Kalo on this PC, one job at a time.

.DESCRIPTION
    Claude writes job files into .kalo\jobs\inbox\ (inside this Kalo folder). The runner picks them up
    in name order, runs their steps, and writes everything to .kalo\jobs\done\<job id>\:
        job.json      the job as received
        log.txt       what ran, when, exit codes
        NN-*.out.txt  / NN-*.err.txt   output of each step
        NN-*.png      screenshots
        result.json   written last: the job is finished when this file exists

    Only these step types exist, nothing else can run:
        dotnet      dotnet build|run|test|restore|clean   (working directory: this Kalo folder)
        exe         an .exe built by Kalo (src\...\bin\...) or a game tool inside .kalo\shared
                    (reach_tag_test, reach_tag_play, tool, tool_fast, sapien)
        screenshot  capture one window (a game tool or Kalo) to a PNG, never the whole desktop
        wait        sleep N seconds
        kill        close game tools or Kalo processes by name
        git         git status|add|commit|log|diff in this Kalo folder
    Every path must stay inside this Kalo folder. HREK and MCC are never touched by the runner.
    While it is open, Windows is kept from going to sleep.

    Start:  powershell -ExecutionPolicy Bypass -File tools\kalo-runner.ps1
    Stop:   Ctrl+C (or close the window)
#>
param([int]$PollSeconds = 2)

$ErrorActionPreference = 'Stop'
$Repo   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
$Jobs   = Join-Path $Repo '.kalo\jobs'
$Inbox  = Join-Path $Jobs 'inbox'
$Done   = Join-Path $Jobs 'done'
$Status = Join-Path $Jobs 'runner-status.json'
$Empty  = Join-Path $Jobs 'empty-input.txt'
$Shared = Join-Path $Repo '.kalo\shared'
New-Item -ItemType Directory -Force -Path $Inbox, $Done | Out-Null
Set-Content -Path $Empty -Value '' -NoNewline

$AllowedDotnet  = @('build', 'run', 'test', 'restore', 'clean')
$AllowedGit     = @('status', 'add', 'commit', 'log', 'diff')
$AllowedGameExe = @('reach_tag_test.exe', 'reach_tag_play.exe', 'tool.exe', 'tool_fast.exe', 'sapien.exe')
$AllowedKill    = @('reach_tag_test', 'reach_tag_play', 'sapien', 'tool', 'tool_fast')

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class KaloRunnerNative {
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@
[KaloRunnerNative]::SetProcessDPIAware() | Out-Null
# ES_CONTINUOUS | ES_SYSTEM_REQUIRED: no sleep while the runner is open.
[KaloRunnerNative]::SetThreadExecutionState([uint32]2147483649) | Out-Null

$Started = Get-Date

function Write-Status([string]$state, [string]$job) {
    $s = [ordered]@{ pid = $PID; started = $Started.ToString('s'); lastPoll = (Get-Date).ToString('s'); state = $state; job = $job; repo = $Repo }
    try { ($s | ConvertTo-Json) | Set-Content -Path $Status -Encoding UTF8 } catch { }
}

function Test-Inside([string]$path, [string]$root) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $r = $root.TrimEnd('\')
    return $full.Equals($r, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-InRepo([string]$relative) {
    $p = if ([IO.Path]::IsPathRooted($relative)) { $relative } else { Join-Path $Repo $relative }
    $full = [IO.Path]::GetFullPath($p)
    if (-not (Test-Inside $full $Repo)) { throw "Path is outside the Kalo folder: $relative" }
    return $full
}

function Quote-Arg([string]$a) {
    if ($a -eq '') { return '""' }
    if ($a -notmatch '[\s"]') { return $a }
    $a = $a -replace '(\\+)$', '$1$1'
    return '"' + ($a -replace '"', '\"') + '"'
}

function Invoke-Tool([string]$file, [object[]]$argList, [string]$cwd, [int]$timeout, [string]$prefix, [bool]$background) {
    $out = "$prefix.out.txt"; $err = "$prefix.err.txt"
    $sp = @{
        FilePath = $file; WorkingDirectory = $cwd; NoNewWindow = $true; PassThru = $true
        RedirectStandardOutput = $out; RedirectStandardError = $err; RedirectStandardInput = $Empty
    }
    $argString = (@($argList) | Where-Object { $_ -ne $null } | ForEach-Object { Quote-Arg ([string]$_) }) -join ' '
    if ($argString) { $sp.ArgumentList = $argString }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process @sp
    $null = $p.Handle  # keeps the exit code readable later
    if ($background) {
        return [ordered]@{ pid = $p.Id; background = $true; exitCode = $null; timedOut = $false; ms = 0; out = (Split-Path $out -Leaf); err = (Split-Path $err -Leaf) }
    }
    $timedOut = -not $p.WaitForExit($timeout * 1000)
    if ($timedOut) { & taskkill.exe /T /F /PID $p.Id 2>&1 | Out-Null }
    $p.WaitForExit()
    return [ordered]@{ pid = $p.Id; background = $false; exitCode = $p.ExitCode; timedOut = $timedOut; ms = $sw.ElapsedMilliseconds; out = (Split-Path $out -Leaf); err = (Split-Path $err -Leaf) }
}

function Save-WindowShot([string]$processName, [string]$path) {
    # Captures one application window (never the whole desktop), even if other windows cover it.
    $p = Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if (-not $p) { throw "no window found for process $processName" }
    $r = New-Object KaloRunnerNative+RECT
    [KaloRunnerNative]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "window of $processName has no size (minimized?)" }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [KaloRunnerNative]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Invoke-Step($step, [int]$index, [string]$dir) {
    $type = [string]$step.type
    $name = if ($step.name) { [string]$step.name } else { $type }
    $name = ($name -replace '[^A-Za-z0-9_.-]', '_')
    $prefix = Join-Path $dir ('{0:D2}-{1}' -f $index, $name)
    $timeout = if ($step.timeout) { [int]$step.timeout } else { 600 }
    $stepArgs = @($step.args)

    switch ($type) {
        'dotnet' {
            if ($stepArgs.Count -eq 0 -or $AllowedDotnet -notcontains [string]$stepArgs[0]) { throw "dotnet subcommand not allowed: $($stepArgs -join ' ')" }
            return Invoke-Tool 'dotnet' $stepArgs $Repo $timeout $prefix ([bool]$step.background)
        }
        'exe' {
            $exe = Resolve-InRepo ([string]$step.path)
            $leaf = Split-Path $exe -Leaf
            $isKaloBuild = (Test-Inside $exe (Join-Path $Repo 'src')) -and ($exe -match '\\bin\\') -and $exe.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)
            $isGameTool = (Test-Inside $exe $Shared) -and ($AllowedGameExe -contains $leaf.ToLowerInvariant()) -and ((Split-Path $exe -Parent).TrimEnd('\') -eq $Shared)
            if (-not ($isKaloBuild -or $isGameTool)) { throw "exe not allowed: $($step.path)" }
            if (-not (Test-Path $exe)) { throw "exe not found: $($step.path)" }
            $cwd = if ($step.cwd) { Resolve-InRepo ([string]$step.cwd) } elseif ($isGameTool) { $Shared } else { $Repo }
            return Invoke-Tool $exe $stepArgs $cwd $timeout $prefix ([bool]$step.background)
        }
        'screenshot' {
            $proc = [string]$step.process
            if (-not (($AllowedKill -contains $proc.ToLowerInvariant()) -or $proc.ToLowerInvariant().StartsWith('kalo'))) { throw "screenshot only of game tools or Kalo windows, not: $proc" }
            $png = "$prefix.png"
            Save-WindowShot $proc $png
            return [ordered]@{ screenshot = (Split-Path $png -Leaf) }
        }
        'wait' {
            $s = if ($step.seconds) { [int]$step.seconds } else { 5 }
            Start-Sleep -Seconds $s
            return [ordered]@{ waited = $s }
        }
        'kill' {
            $killed = @()
            foreach ($n in @($step.names)) {
                $n = [string]$n
                if (-not (($AllowedKill -contains $n.ToLowerInvariant()) -or $n.ToLowerInvariant().StartsWith('kalo'))) { throw "kill not allowed: $n" }
                foreach ($p in @(Get-Process -Name $n -ErrorAction SilentlyContinue)) { Stop-Process -Id $p.Id -Force; $killed += "$n($($p.Id))" }
            }
            return [ordered]@{ killed = $killed }
        }
        'git' {
            if ($stepArgs.Count -eq 0 -or $AllowedGit -notcontains [string]$stepArgs[0]) { throw "git subcommand not allowed: $($stepArgs -join ' ')" }
            return Invoke-Tool 'git' $stepArgs $Repo $timeout $prefix $false
        }
        default { throw "unknown step type: $type" }
    }
}

function Invoke-Job([IO.FileInfo]$file) {
    $job = $null
    for ($i = 0; $i -lt 5 -and $job -eq $null; $i++) {
        try { $job = Get-Content -Raw -Path $file.FullName | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 500 }
    }
    $id = ($file.Name -replace '\.job\.json$', '') -replace '[^A-Za-z0-9_.-]', '_'
    $dir = Join-Path $Done $id
    if (Test-Path $dir) { $dir = "$dir-" + (Get-Date -Format 'HHmmss') }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Move-Item -Path $file.FullName -Destination (Join-Path $dir 'job.json')
    $log = Join-Path $dir 'log.txt'

    $result = [ordered]@{ id = $id; started = (Get-Date).ToString('s'); finished = $null; ok = $true; steps = @() }
    Write-Host ""
    Write-Host "== job $id" -ForegroundColor Cyan
    if ($job -eq $null) {
        $result.ok = $false; $result.error = 'job file is not valid JSON'
    } else {
        Write-Status 'running' $id
        $index = 0
        foreach ($step in @($job.steps)) {
            $index++
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $entry = [ordered]@{ index = $index; type = [string]$step.type; name = [string]$step.name }
            try {
                $r = Invoke-Step $step $index $dir
                foreach ($k in $r.Keys) { $entry[$k] = $r[$k] }
                $failed = ($r.Contains('exitCode') -and $r.exitCode -ne $null -and $r.exitCode -ne 0) -or ($r.Contains('timedOut') -and $r.timedOut)
            } catch {
                $entry.error = $_.Exception.Message
                $failed = $true
            }
            $entry.totalMs = $sw.ElapsedMilliseconds
            $result.steps += $entry
            $line = '{0} step {1} {2} {3} -> {4}' -f (Get-Date -Format 'HH:mm:ss'), $index, $entry.type, $entry.name, ($(if ($failed) { 'FAILED' } else { 'ok' }))
            if ($entry.error) { $line += " ($($entry.error))" }
            Add-Content -Path $log -Value $line
            Write-Host $line -ForegroundColor $(if ($failed) { 'Yellow' } else { 'Gray' })
            if ($failed -and -not $step.allowFail -and -not $job.continueOnError) { $result.ok = $false; break }
            if ($failed) { $result.ok = $false }
        }
    }
    $result.finished = (Get-Date).ToString('s')
    ($result | ConvertTo-Json -Depth 6) | Set-Content -Path (Join-Path $dir 'result.json') -Encoding UTF8
    Write-Host "== job $id finished: $(if ($result.ok) { 'ok' } else { 'with failures' })" -ForegroundColor Cyan
}

Write-Host "Kalo runner started in $Repo" -ForegroundColor Green
Write-Host "Watching $Inbox. Leave this window open; Ctrl+C to stop." -ForegroundColor Green
try {
    while ($true) {
        Write-Status 'idle' $null
        $next = Get-ChildItem -Path $Inbox -Filter '*.job.json' -File | Sort-Object Name | Select-Object -First 1
        if ($next) {
            try { Invoke-Job $next }
            catch { Write-Host "Job error: $($_.Exception.Message)" -ForegroundColor Red }
        } else {
            Start-Sleep -Seconds $PollSeconds
        }
    }
} finally {
    Write-Status 'stopped' $null
    [KaloRunnerNative]::SetThreadExecutionState([uint32]2147483648) | Out-Null
}
