#!/usr/bin/env pwsh
param(
    [string]$GodotPath = "",
    [string]$PythonPath = "python",
    [string]$ArenaRoot = "",
    [switch]$NoRestore
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$restoreArgs = @()
if ($NoRestore) { $restoreArgs = @("--no-restore") }

function Assert-Exit([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed (exit $LASTEXITCODE)." }
}

Write-Host "Simulation thread and dependency scan"
& $PythonPath -X utf8 (Join-Path $PSScriptRoot "check_sim_thread.py")
Assert-Exit "Simulation scan"

Write-Host "Build RTSarcade"
dotnet build (Join-Path $repoRoot "RTSarcade.csproj") -c Debug --nologo -v q @restoreArgs
Assert-Exit "RTSarcade build"

foreach ($configuration in @("Debug", "Release")) {
    Write-Host "RTSarcade determinism tests ($configuration)"
    dotnet run --project (Join-Path $PSScriptRoot "SimulationDeterminismTest/SimulationDeterminismTest.csproj") -c $configuration @restoreArgs
    Assert-Exit "Determinism $configuration"
}

if ($GodotPath) {
    Write-Host "Godot configuration and simulation wiring smoke test"
    $smokeOutput = & $GodotPath --headless --path $repoRoot --quit-after 600 res://Scenes/Test/RefactorSmokeTest.tscn -- --offline 2>&1
    $smokeExit = $LASTEXITCODE
    $smokeOutput | ForEach-Object { Write-Host $_ }
    if ($smokeExit -ne 0 -or -not ($smokeOutput -match "REFACTOR_SMOKE_PASS")) {
        throw "Godot smoke test failed or timed out."
    }
    $raceOutput = & $GodotPath --headless --path $repoRoot --quit-after 600 res://Scenes/Test/RaceAISmokeTest.tscn -- --offline 2>&1
    $raceExit = $LASTEXITCODE
    $raceOutput | ForEach-Object { Write-Host $_ }
    if ($raceExit -ne 0 -or -not ($raceOutput -match "RACE_AI_SMOKE_PASS")) {
        throw "Race AI smoke test failed or timed out."
    }
} else {
    Write-Host "SKIP: Godot runtime smoke test (supply -GodotPath)."
}

# The external Arena project is optional and does not substitute RTSarcade tests.
if ($ArenaRoot) {
    dotnet run --project (Join-Path $ArenaRoot "Tools/SimulationSelfTest/SimulationSelfTest.csproj") -c Debug @restoreArgs -- (Join-Path $ArenaRoot "Data/Tables")
    Assert-Exit "Arena self-test"
    if ($GodotPath) {
        & (Join-Path $ArenaRoot "Tools/Test-Network.ps1") -GodotPath $GodotPath
        Assert-Exit "Arena network test"
    } else {
        Write-Host "SKIP: Arena network test (supply -GodotPath)."
    }
} else {
    Write-Host "SKIP: external Arena tests (supply -ArenaRoot)."
}
Write-Host "CI_PASS"
