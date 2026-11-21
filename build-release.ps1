$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$Proj     = 'src\FH6RB.csproj'
$Tools    = 'tools'
$Zip      = Join-Path $PSScriptRoot 'fhre.zip'
$RunId    = [Guid]::NewGuid().ToString('N')
$BuildOut = Join-Path $PSScriptRoot "bin\release-$RunId"
$zipTmp   = "$Zip.$RunId.tmp"
$requiredTools = @('ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe', 'fsbank/fsbankcl.exe', 'vgmstream/vgmstream-cli.exe')

function Fail($msg) {
    Write-Host ''
    Write-Host '============================================' -ForegroundColor Red
    Write-Host "  BUILD FAILED: $msg" -ForegroundColor Red
    Write-Host '============================================' -ForegroundColor Red
    exit 1
}

function Remove-PathWithRetry([string]$Path, [int]$Attempts = 6, [int]$DelayMs = 500) {
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop }
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

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet SDK not found in PATH.' }
if (-not (Test-Path -LiteralPath $Proj -PathType Leaf)) { Fail "project not found: $Proj." }
$missing = @($requiredTools | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Tools $_) -PathType Leaf) })
if ($missing.Count -gt 0) { Fail ("required tools missing: " + ($missing -join ', ')) }

function Open-ZipSource([string]$Path) {
    for ($attempt = 1; $attempt -le 15; $attempt++) {
        try {
            return [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        } catch {
            if ($attempt -eq 15) { throw }
            if ($attempt -eq 1) { Write-Host "  Waiting for $Path" }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Write-ReleaseZip {
    $stream = [System.IO.File]::Open($zipTmp, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($rel in $filesToZip.Keys) {
                $in = Open-ZipSource $filesToZip[$rel]
                try {
                    $entry = $archive.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
                    $out = $entry.Open()
                    try { $in.CopyTo($out) } finally { $out.Dispose() }
                } finally { $in.Dispose() }
            }
        } finally { $archive.Dispose() }
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

try {
    Write-Host ''
    Write-Host '[1/2] Building single-file exe ...'
    dotnet publish $Proj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $BuildOut
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $BuildOut 'fhre.exe') -PathType Leaf)) {
        throw 'fhre.exe is missing from the publish output.'
    }

    Write-Host ''
    Write-Host "[2/2] Creating $Zip ..."
    Add-Type -AssemblyName System.IO.Compression
    $filesToZip = [ordered]@{}
    foreach ($source in @($BuildOut, $Tools)) {
        $sourceRoot = (Resolve-Path -LiteralPath $source).ProviderPath.TrimEnd('\')
        foreach ($file in (Get-ChildItem -LiteralPath $sourceRoot -Recurse -File)) {
            $rel = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
            if ($source -eq $Tools -and $rel -eq 'ffmpeg/ffplay.exe') { continue }
            if ($filesToZip.Contains($rel)) { throw "duplicate archive entry: $rel." }
            $filesToZip.Add($rel, $file.FullName)
        }
    }
    foreach ($required in $requiredTools) {
        if (-not $filesToZip.Contains($required)) { throw "required tool missing from the archive: $required." }
    }

    for ($i = 1; $i -le 3; $i++) {
        try {
            Write-ReleaseZip
            break
        } catch {
            if ($i -eq 3) { throw }
            Write-Host "  Rebuilding archive after an error: $($_.Exception.Message)"
            Start-Sleep -Milliseconds 500
        }
    }

    for ($i = 1; $i -le 10; $i++) {
        try {
            if ([System.IO.File]::Exists($Zip)) {
                [System.IO.File]::Replace($zipTmp, $Zip, [NullString]::Value)
            } else {
                [System.IO.File]::Move($zipTmp, $Zip)
            }
            break
        } catch {
            if ($i -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
} catch {
    try { [System.IO.File]::Delete($zipTmp) } catch { Write-Warning "Could not remove $zipTmp" }
    Fail $_.Exception.Message
} finally {
    if (-not (Remove-PathWithRetry $BuildOut)) { Write-Warning "Could not remove $BuildOut" }
}

Write-Host ''
Write-Host '============================================' -ForegroundColor Green
Write-Host "  DONE  ->  $Zip" -ForegroundColor Green
Write-Host '============================================' -ForegroundColor Green
exit 0
