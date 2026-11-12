#requires -version 3.0
# LogSentry portable packer: custom launcher stub + zip payload.
#
# Output: dist\LogSentry_Portable.exe -- one file. Double-click extracts the payload
# to %TEMP%\LogSentry_xxxx and starts LogSentry.exe (which self-elevates via its own
# requireAdministrator manifest). The temp folder is removed after the app exits.
#
# Why not 7-Zip SFX any more:
#   The official 7z.sfx / 7zS.sfx / 7zCon.sfx modules are plain extractors. They ignore
#   the RunProgram / Directory / InstallPath keys of the ;!@Install@! config block
#   (measured: a package with RunProgram="cmd.exe /c echo X > mark.txt" exits 0 and
#   never creates mark.txt; 7zCon.sfx only understands -o). The modules that do run an
#   install script are third-party rebuilds (7zSD.sfx), not shipped in the official
#   extra package. So this uses a tiny C# stub (src\Launcher.cs) instead: no external
#   tool, no extra runtime.
#
# Payload is a plain zip: .NET's own System.IO.Compression.ZipArchive reads it, so the
# target machine needs no 7z.exe at all.
#
# KEEP THIS FILE PURE ASCII. Windows PowerShell 5 decodes BOM-less scripts as ANSI
# (GBK on a Chinese system), and a stray multi-byte sequence can eat a quote and break
# the whole script. Comments stay in English for that reason.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-portable.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-portable.ps1 -IncludeDotNet
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-portable.ps1 -OutputName Test.exe

param(
    [string]$DistDir = "dist",
    [string]$OutputName = "LogSentry_Portable.exe",
    # Bundle the .NET Framework 4 offline installer into the payload (~1 MB -> ~49 MB).
    [switch]$IncludeDotNet,
    [string]$DotNetInstaller = "dotNetFx40_Full_x86_x64.exe",
    # Keep dist smoke artifacts out of the payload (default behaviour).
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

function Say($m) { Write-Host $m }

$dist = Join-Path $root $DistDir
$appExe = Join-Path $dist "LogSentry.exe"
if (-not (Test-Path $appExe)) { throw ("Build first: missing " + $appExe) }
if (-not (Test-Path (Join-Path $dist "LogParser.exe"))) {
    Say "[!] warning: LogParser.exe is not in dist - the package will start but cannot query."
}

# ---- 1. stage the payload
$stageRoot = Join-Path $root ".pkg\payload"
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

$payload = @("LogSentry.exe", "LogParser.exe", "LogParser.chm", "queries.txt")
foreach ($f in $payload) {
    $p = Join-Path $dist $f
    if (Test-Path $p) { Copy-Item $p $stageRoot -Force; Say ("    + " + $f) }
    else { Say ("    - skip (missing): " + $f) }
}

# the user manual: any *.txt in dist except queries.txt and smoke artifacts
$manuals = @(Get-ChildItem -Path $dist -Filter *.txt -File |
    Where-Object { $_.Name -ne "queries.txt" -and $_.Name -ne "ui-results.txt" })
foreach ($m in $manuals) { Copy-Item $m.FullName $stageRoot -Force; Say ("    + " + $m.Name) }

if ($IncludeDotNet) {
    $dn = Join-Path $root $DotNetInstaller
    if (-not (Test-Path $dn)) { throw ("-IncludeDotNet but installer not found: " + $dn) }
    Copy-Item $dn $stageRoot -Force
    Say ("    + " + $DotNetInstaller + " (" + [math]::Round((Get-Item $dn).Length / 1MB, 1) + " MB)")
}

# ---- 2. zip it (Deflate; the stub reads it through ZipArchive)
$zipPath = Join-Path $root ".pkg\payload.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Say ("[*] payload  : " + $zipPath + " (" + [math]::Round((Get-Item $zipPath).Length / 1KB, 1) + " KB)")

# ---- 3. compile the launcher stub (same csc as the app, no extra toolchain)
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe not found (needs .NET Framework 4.x)." }

$fwDir = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path $fwDir)) { $fwDir = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319" }
$refs = @()
foreach ($asm in @("System.dll", "System.Core.dll", "System.Windows.Forms.dll", "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll")) {
    $full = Join-Path $fwDir $asm
    if (Test-Path $full) { $refs += "/r:$full" }
}
$launcherExe = Join-Path $root ".pkg\LogSentryLauncher.exe"
$src = Join-Path $root "src\Launcher.cs"
$manifest = Join-Path $root "src\launcher.manifest"
$buildArgs = @("/nologo", "/utf8output", "/codepage:65001", "/optimize+", "/target:winexe", "/platform:anycpu") +
    @("/win32manifest:$manifest", "/out:$launcherExe") + $refs + @($src)
& $csc @buildArgs
if ($LASTEXITCODE -ne 0) { throw ("launcher compile failed, exit code " + $LASTEXITCODE) }
Say ("[*] launcher : " + $launcherExe + " (" + [math]::Round((Get-Item $launcherExe).Length / 1KB, 1) + " KB)")

# ---- 4. concatenate: launcher + "LSPAYLOAD!" + zip
$out = Join-Path $dist $OutputName
if (Test-Path $out) { Remove-Item $out -Force }
$buffer = New-Object System.IO.MemoryStream
$launcherBytes = [System.IO.File]::ReadAllBytes($launcherExe)
$buffer.Write($launcherBytes, 0, $launcherBytes.Length)
$marker = [System.Text.Encoding]::ASCII.GetBytes("LSPAYLOAD!")
$buffer.Write($marker, 0, $marker.Length)
$zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
$buffer.Write($zipBytes, 0, $zipBytes.Length)
[System.IO.File]::WriteAllBytes($out, $buffer.ToArray())
$buffer.Dispose()

$sizeMB = [math]::Round((Get-Item $out).Length / 1MB, 2)
Say ("[OK] " + $out + "  (" + $sizeMB + " MB)")
$hash = (Get-FileHash $out -Algorithm SHA256).Hash
Say ("     SHA256 " + $hash)
Say ""
Say ("Payload: " + ($payload -join ", ") + $(if ($IncludeDotNet) { ", " + $DotNetInstaller } else { "" }))
Say "Run: double-click -> extract to %TEMP%\LogSentry_<pid>_<ticks> -> launch LogSentry.exe -> clean up on exit"
Say "Debug: LogSentry_Portable.exe --keep  extracts to .\LogSentry_unpacked\ next to the exe and keeps it"
