
# Builds the client and server release packages, then deploys them to each available SPT install.
# Existing install config files are preserved; unavailable or locked paths are skipped with a warning.

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Join-Path $repoRoot 'Build\KeepMeAlive'
$bepinDir    = Join-Path $packageRoot 'BepInEx'
$sptDir      = Join-Path $packageRoot 'SPT_Runtime'
$deployRoots = @('C:\SPT', 'Y:\SPT', 'Y:\NsmServers\Tarkov2.0Test\Host1')

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

# Package folder -> install folder, relative to the package / install root.
$deployDirs = @(
    'BepInEx\plugins',
    'SPT_Runtime\user\mods\KeepMeAlive'
)

$skipped = @()
foreach ($root in $deployRoots) {
    if (-not (Test-Path $root)) {
        Write-Warning "Skipping $root (not found)"
        $skipped += $root
        continue
    }

    Write-Host "Deploying to $root" -ForegroundColor Cyan
    # Each part on its own, so a locked server mod does not hide that the client plugin went in (and vice versa).
    foreach ($dir in $deployDirs) {
        $target = Join-Path $root $dir
        try {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -Path (Join-Path (Join-Path $packageRoot $dir) '*') -Destination $target -Recurse -Force
        }
        catch {
            Write-Warning "Deploy to $target failed (is the game or server running?): $($_.Exception.Message)"
            $skipped += $target
        }
    }
}

if ($skipped.Count -gt 0) {
    Write-Host "Not deployed to: $($skipped -join ', ')" -ForegroundColor Yellow
}
else {
    Write-Host "Deployed to: $($deployRoots -join ', ')" -ForegroundColor Green
}
