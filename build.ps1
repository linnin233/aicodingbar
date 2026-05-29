# build.ps1 — Build and package ClaudeMonitor for release
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version "1.0.0"
#
# Output: dist/ClaudeMonitor-v{version}.zip

param(
    [string]$Version = "0.1.0",
    [switch]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$RootDir = $PSScriptRoot
$ProjectDir = Join-Path $RootDir "taskbar-monitor"
$HooksDir = Join-Path $RootDir "hooks"
$DistDir = Join-Path $RootDir "dist"
$StagingDir = Join-Path $DistDir "staging"
$ZipName = "ClaudeMonitor-v$Version.zip"

Write-Host "=== ClaudeMonitor Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Self-contained: $SelfContained"
Write-Host ""

# Step 1: Publish
Write-Host "[1/4] Publishing..." -ForegroundColor Yellow

$publishArgs = @(
    "publish", $ProjectDir,
    "-c", "Release",
    "-r", "win-x64",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "--nologo"
)

if ($SelfContained) {
    $publishArgs += "--self-contained", "true"
} else {
    $publishArgs += "--self-contained", "false"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed!"; exit 1 }

$publishDir = Join-Path $ProjectDir "bin\Release\net8.0-windows\win-x64\publish"

# Step 2: Stage files
Write-Host "[2/4] Staging files..." -ForegroundColor Yellow

if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

# Copy executable
Copy-Item (Join-Path $publishDir "taskbar-monitor.exe") $StagingDir

# Copy hooks
$stagingHooks = Join-Path $StagingDir "hooks"
Copy-Item $HooksDir $stagingHooks -Recurse

# Copy docs
Copy-Item (Join-Path $RootDir "README.md") $StagingDir
Copy-Item (Join-Path $RootDir "README-zh.md") $StagingDir
Copy-Item (Join-Path $RootDir "LICENSE") $StagingDir -ErrorAction SilentlyContinue

# Step 3: Create zip
Write-Host "[3/4] Creating zip..." -ForegroundColor Yellow

$zipPath = Join-Path $DistDir $ZipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path "$StagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

# Step 4: Cleanup staging
Write-Host "[4/4] Cleaning up..." -ForegroundColor Yellow
Remove-Item $StagingDir -Recurse -Force

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host "Output: $zipPath ($zipSize MB)" -ForegroundColor Green
