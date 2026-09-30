
# KeepMeAlive build script
# Wipes the BepInEx and SPT_Runtime output folders, then builds both
# projects (Release). Each project's PostBuild step copies its own output
# into Build\KeepMeAlive, and the client also deploys straight to the live
# SPT install (SPTBaseDir, default C:\SPT).

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Join-Path $repoRoot 'Build\KeepMeAlive'
$bepinDir    = Join-Path $packageRoot 'BepInEx'
$sptDir      = Join-Path $packageRoot 'SPT_Runtime'

Set-Location $repoRoot

Write-Host "Deleting BepInEx and SPT_Runtime folders" -ForegroundColor Cyan
Remove-Item -Recurse -Force $bepinDir -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $sptDir -ErrorAction SilentlyContinue

Write-Host "Recreating output folders" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $bepinDir 'plugins') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $sptDir 'user\mods\KeepMeAlive') | Out-Null

Write-Host "Building KeepMeAlive-Core (Release)" -ForegroundColor Cyan
dotnet build 'KeepMeAlive-Core/KeepMeAlive.csproj' --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "KeepMeAlive-Core build failed" }

Write-Host "Building KeepMeAlive-Server (Release)" -ForegroundColor Cyan
dotnet build 'KeepMeAlive-Server/KeepMeAlive.Server.csproj' --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "KeepMeAlive-Server build failed" }

Write-Host "Build complete: $packageRoot" -ForegroundColor Green
