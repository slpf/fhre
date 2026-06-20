# ============================================================
#  FHRE release build script (PowerShell)
#  Place in project root (next to "src" and "tools").
#  Run:  powershell -ExecutionPolicy Bypass -File .\build-release.ps1
# ============================================================

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$Proj     = 'src\FH6RB.csproj'
$Bin      = 'bin'
$BuildOut = 'bin\publish'
$Dist     = 'publish'
$Tools    = 'tools'
$Zip      = 'fhre.zip'

function Fail($msg) {
    Write-Host ''
    Write-Host '============================================' -ForegroundColor Red
    Write-Host "  BUILD FAILED: $msg" -ForegroundColor Red
    Write-Host '============================================' -ForegroundColor Red
    exit 1
}

Write-Host '============================================'
Write-Host '  FHRE release build'
Write-Host '============================================'

# --- prerequisites ---
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet SDK not found in PATH.' }
if (-not (Test-Path $Proj))  { Fail "project not found: $Proj (run from project root)." }
if (-not (Test-Path $Tools)) { Fail "tools folder not found: $Tools." }

# --- clean previous outputs ---
if (Test-Path $Dist)     { Write-Host "Removing old $Dist ...";     Remove-Item -Recurse -Force $Dist }
if (Test-Path $BuildOut) { Write-Host "Removing old $BuildOut ..."; Remove-Item -Recurse -Force $BuildOut }
if (Test-Path $Zip)      { Remove-Item -Force $Zip }

# --- [1/5] publish ---
Write-Host ''
Write-Host '[1/5] Building single-file exe ...'
dotnet publish $Proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $BuildOut
if ($LASTEXITCODE -ne 0)   { Fail 'dotnet publish failed.' }
if (-not (Test-Path $BuildOut)) { Fail "build output not found: $BuildOut." }

# --- [2/5] move build output to publish ---
Write-Host ''
Write-Host "[2/5] Moving build output to $Dist ..."
Move-Item -Path $BuildOut -Destination $Dist
if (-not (Test-Path $Dist)) { Fail "move to $Dist failed." }

# --- [3/5] copy tools into publish ---
Write-Host ''
Write-Host "[3/5] Copying $Tools into $Dist ..."
Copy-Item -Path (Join-Path $Tools '*') -Destination $Dist -Recurse -Force

# --- [4/5] zip ---
Write-Host ''
Write-Host "[4/5] Creating $Zip ..."
Compress-Archive -Path (Join-Path $Dist '*') -DestinationPath $Zip -Force
if (-not (Test-Path $Zip)) { Fail "zip not created: $Zip." }

# --- [5/5] cleanup ---
Write-Host ''
Write-Host '[5/5] Cleaning up ...'
if (Test-Path $Bin)  { Remove-Item -Recurse -Force $Bin }
if (Test-Path $Dist) { Remove-Item -Recurse -Force $Dist }

Write-Host ''
Write-Host '============================================' -ForegroundColor Green
Write-Host "  DONE  ->  $Zip" -ForegroundColor Green
Write-Host '============================================' -ForegroundColor Green
exit 0