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

# Antivirus often holds freshly written executables for a moment;
# retry removal instead of failing the build on a transient lock.
function Remove-PathWithRetry([string]$Path, [int]$Attempts = 6, [int]$DelayMs = 500) {
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            if (Test-Path $Path) { Remove-Item -Recurse -Force $Path -ErrorAction Stop }
            return $true
        } catch {
            if ($i -lt $Attempts) { Start-Sleep -Milliseconds $DelayMs }
        }
    }
    return $false
}

Write-Host '============================================'
Write-Host '  FHRE release build'
Write-Host '============================================'

# --- prerequisites ---
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet SDK not found in PATH.' }
if (-not (Test-Path $Proj))  { Fail "project not found: $Proj (run from project root)." }
if (-not (Test-Path $Tools)) { Fail "tools folder not found: $Tools." }

# --- clean previous outputs ---
if (Test-Path $Dist)     { Write-Host "Removing old $Dist ...";     if (-not (Remove-PathWithRetry $Dist))     { Fail "cannot remove old $Dist - locked by another process (antivirus?). Close it and retry." } }
if (Test-Path $BuildOut) { Write-Host "Removing old $BuildOut ..."; if (-not (Remove-PathWithRetry $BuildOut)) { Fail "cannot remove old $BuildOut - locked by another process (antivirus?). Close it and retry." } }
if (Test-Path $Zip)      { if (-not (Remove-PathWithRetry $Zip)) { Fail "cannot remove old $Zip - locked by another process." } }

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

Get-ChildItem -Path $Dist -Filter *.pdb -File | ForEach-Object { Remove-Item -Force $_.FullName }

# --- [3/5] copy tools into publish ---
Write-Host ''
Write-Host "[3/5] Copying $Tools into $Dist ..."
Copy-Item -Path (Join-Path $Tools '*') -Destination $Dist -Recurse -Force

# --- [4/5] zip ---
Write-Host ''
Write-Host "[4/5] Creating $Zip ..."

# Freshly copied executables are often briefly locked by an antivirus scan,
# which aborts Compress-Archive halfway through (and the half-written zip then
# gets locked too). Zip entry by entry with per-file retries into a temp file,
# then rename at the end.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zipTmp = "$Zip.tmp"
if ((Test-Path $zipTmp) -and (-not (Remove-PathWithRetry $zipTmp))) { Fail "cannot remove stale $zipTmp - locked by another process." }

$filesToZip = @(Get-ChildItem -Path $Dist -Recurse -File | ForEach-Object { $_.FullName })
$distRoot = (Resolve-Path $Dist).ProviderPath.TrimEnd('\')

$zipStream = [System.IO.File]::Open($zipTmp, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
$archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    foreach ($src in $filesToZip) {
        $rel = $src.Substring($distRoot.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
        $copied = $false

        for ($i = 1; ($i -le 15) -and (-not $copied); $i++) {
            try {
                $in = [System.IO.File]::Open($src, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                try {
                    $es = $entry.Open()
                    try { $in.CopyTo($es) } finally { $es.Dispose() }
                    $copied = $true
                } finally { $in.Close() }
            } catch {
                if ($i -eq 1) { Write-Host "  $rel is locked (antivirus?), retrying ..." }
                if ($i -lt 15) { Start-Sleep -Milliseconds 500 }
            }
        }

        if (-not $copied) { Fail "cannot read $rel - locked by another process (antivirus?). Add a project-folder exclusion and retry." }
    }
} finally {
    $archive.Dispose()
    $zipStream.Dispose()
}

$moved = $false
for ($i = 1; ($i -le 10) -and (-not $moved); $i++) {
    try {
        if (Test-Path $Zip) { Remove-Item -Force $Zip -ErrorAction Stop }
        Move-Item -Path $zipTmp -Destination $Zip -ErrorAction Stop
        $moved = $true
    } catch {
        if ($i -lt 10) { Start-Sleep -Milliseconds 500 }
    }
}

if (-not $moved)           { Fail "cannot finalize $Zip - locked by another process (antivirus?)." }
if (-not (Test-Path $Zip)) { Fail "zip not created: $Zip." }

# --- [5/5] cleanup ---
Write-Host ''
Write-Host '[5/5] Cleaning up ...'
if (-not (Remove-PathWithRetry $Bin))  { Write-Host "  WARNING: could not remove $Bin (locked) - remove it manually." -ForegroundColor Yellow }
if (-not (Remove-PathWithRetry $Dist)) { Write-Host "  WARNING: could not remove $Dist (locked) - remove it manually." -ForegroundColor Yellow }

Write-Host ''
Write-Host '============================================' -ForegroundColor Green
Write-Host "  DONE  ->  $Zip" -ForegroundColor Green
Write-Host '============================================' -ForegroundColor Green
exit 0