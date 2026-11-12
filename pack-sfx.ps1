#requires -version 3.0
# LogSentry single-file packer (7-Zip SFX). Back to the simple route after 2026-11-12.
#
# Output: dist\LogSentry_Portable.exe -- one file.
#   Double-click -> 7-Zip's own "Extract to:" dialog (the official 7z.sfx is a plain GUI
#   extractor, it ignores the ;!@Install@! config block -- measured, see sfx-config.txt)
#   -> user picks a folder, clicks Extract, then runs LogSentry\LogSentry.exe themselves.
#   The payload also carries dotNetFx40_Full_x86_x64.exe as a normal file, so a machine
#   without .NET Framework 4 can install it by hand and retry. No auto-launch, no
#   detection, no installer orchestration -- that is deliberate, by user request.
#
# Requirements:
#   - 7z.exe or 7za.exe (any recent 7-Zip build). Searched: Program Files, .tools\extra,
#     .tools, then PATH. This repo ships .tools\extra\7za.exe, so nothing has to be installed.
#   - a 7z SFX module: 7z.sfx (GUI) in .tools\sfx\, or pass -SfxModule.
#
# KEEP THIS FILE PURE ASCII: Windows PowerShell 5 decodes BOM-less scripts as ANSI, and a
# stray multi-byte sequence can eat a quote and break the whole script. Chinese text lives
# in sfx-config.txt (read explicitly as UTF-8) and dist\使用说明.txt only.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-sfx.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-sfx.ps1 -SfxModule .tools\sfx\7z.sfx

param(
    [string]$DistDir = "dist",
    [string]$OutputName = "LogSentry_Portable.exe",
    [string]$SfxModule = "",
    [string]$SevenZip = "",
    [string]$DotNetInstaller = "dotNetFx40_Full_x86_x64.exe",
    # 5 is plenty: the .NET installer is already cab-compressed, -mx=9 only shaves ~2 MB
    # and takes much longer.
    [int]$CompressionLevel = 5
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

function Say($m) { Write-Host $m }

function Find-7Zip {
    if ($SevenZip -and (Test-Path $SevenZip)) { return (Resolve-Path $SevenZip).Path }
    $cands = @(
        (Join-Path ${env:ProgramFiles} "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe"),
        (Join-Path $root ".tools\extra\7za.exe"),
        (Join-Path $root ".tools\7za.exe"),
        (Join-Path $root ".tools\7z.exe")
    )
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path } }
    foreach ($n in @("7z.exe", "7za.exe")) {
        $cmd = Get-Command $n -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    throw "7z.exe / 7za.exe not found. Put 7za.exe in .tools\extra\ or pass -SevenZip <path>."
}

function Find-Sfx {
    if ($SfxModule -and (Test-Path $SfxModule)) { return (Resolve-Path $SfxModule).Path }
    $cands = @(
        (Join-Path $root ".tools\sfx\7z.sfx"),
        (Join-Path ${env:ProgramFiles} "7-Zip\7z.sfx"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.sfx")
    )
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path } }
    throw "SFX module not found. Put 7z.sfx into .tools\sfx\ or pass -SfxModule <path>."
}

$dist = Join-Path $root $DistDir
$appExe = Join-Path $dist "LogSentry.exe"
if (-not (Test-Path $appExe)) { throw "Build first: LogSentry.exe missing in $dist" }
if (-not (Test-Path (Join-Path $dist "LogParser.exe"))) {
    Say "[!] warning: LogParser.exe is not in dist - the package will start but cannot query."
}

$sz = Find-7Zip
$sfx = Find-Sfx
Say ("[*] 7z  : " + $sz)
Say ("[*] sfx : " + $sfx)

# ---- staging: only the files the user needs, all inside one folder
# Everything goes into stage\LogSentry\ so the payload extracts as a folder
# (.\LogSentry\...) instead of scattering loose files in the chosen directory.
$stageRoot = Join-Path $root ".pkg\stage"
$stage = Join-Path $stageRoot "LogSentry"
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$payload = @("LogSentry.exe", "LogParser.exe", "LogParser.chm", "queries.txt")
foreach ($f in $payload) {
    $p = Join-Path $dist $f
    if (Test-Path $p) { Copy-Item $p $stage -Force; Say ("    + " + $f) }
    else { Say ("    - skip (missing): " + $f) }
}

# the user manual: any *.txt in dist that is not queries.txt and not a smoke artifact
$manuals = @(Get-ChildItem -Path $dist -Filter *.txt -File |
    Where-Object { $_.Name -ne "queries.txt" -and $_.Name -ne "ui-results.txt" })
foreach ($m in $manuals) {
    Copy-Item $m.FullName $stage -Force
    Say ("    + manual: " + $m.Name + " (" + $m.Length + " bytes)")
}
if ($manuals.Count -eq 0) { Say ("    - no manual found in dist") }

# the .NET Framework 4 offline installer, shipped as a plain file next to the app.
# dist\ first, then the repo root (the root copy is git-ignored, dist is the deliverable).
$dn = Join-Path $dist $DotNetInstaller
if (-not (Test-Path $dn)) { $dn = Join-Path $root $DotNetInstaller }
if (Test-Path $dn) {
    Copy-Item $dn $stage -Force
    Say ("    + " + $DotNetInstaller + " (" + [math]::Round((Get-Item $dn).Length / 1MB, 1) + " MB)")
} else {
    Say ("[!] warning: " + $DotNetInstaller + " not found in dist\ or repo root - package built without it")
}

# ---- archive
$archive = Join-Path $root ".pkg\payload.7z"
if (Test-Path $archive) { Remove-Item $archive -Force }
Push-Location $stageRoot
try {
    & $sz a -t7z "-mx=$CompressionLevel" -mhc=on "-w$root\.pkg" $archive "LogSentry" | Out-Null
} finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw ("7z failed, exit code " + $LASTEXITCODE) }
$archiveSize = (Get-Item $archive).Length
Say ("[*] archive: " + $archive + " (" + $archiveSize + " bytes)")

# ---- SFX config (UTF-8 WITHOUT BOM - mandatory, the stub parses the first line literally)
# The Chinese values live in sfx-config.txt and are read with -Encoding UTF8 on purpose:
# this .ps1 may be decoded as ANSI by the host, which would mangle non-ASCII literals
# written directly in here (that is exactly how the title turned into mojibake once).
$cfgTemplate = Join-Path $root "sfx-config.txt"
if (-not (Test-Path $cfgTemplate)) { throw "sfx-config.txt not found: $cfgTemplate" }
$body = @(Get-Content -LiteralPath $cfgTemplate -Encoding UTF8 |
    Where-Object { $_.Trim().Length -gt 0 -and -not $_.TrimStart().StartsWith(";") })
$lines = @(';!@Install@!UTF-8!') + $body + @(';!@InstallEnd@!')
$cfg = Join-Path $root ".pkg\config.txt"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($cfg, ($lines -join "`r`n") + "`r`n", $utf8NoBom)

# sanity: must not start with a BOM
$head = [System.IO.File]::ReadAllBytes($cfg)
if ($head.Length -ge 3 -and $head[0] -eq 0xEF -and $head[1] -eq 0xBB -and $head[2] -eq 0xBF) {
    throw "config.txt has a BOM - the SFX stub will not recognise it."
}
Say ("[*] config : UTF-8 no BOM, " + (Get-Item $cfg).Length + " bytes")

# ---- concatenate: sfx + config + archive
$out = Join-Path $dist $OutputName
if (Test-Path $out) { Remove-Item $out -Force }
$buffer = New-Object System.IO.MemoryStream
foreach ($part in @($sfx, $cfg, $archive)) {
    $bytes = [System.IO.File]::ReadAllBytes($part)
    $buffer.Write($bytes, 0, $bytes.Length)
}
[System.IO.File]::WriteAllBytes($out, $buffer.ToArray())
$buffer.Dispose()

$sizeMB = [math]::Round((Get-Item $out).Length / 1MB, 2)
Say ("[OK] " + $out + "  (" + $sizeMB + " MB)")
Say ("     SHA256 " + (Get-FileHash $out -Algorithm SHA256).Hash)
Say ""
Say "Layout: 7z.sfx + config.txt + payload.7z  (payload root folder: LogSentry\)"
Say "Use   : double-click -> 'Extract to:' dialog -> pick a folder (e.g. D:\LogSentry) -> Extract"
Say "        -> no .NET 4 ? run LogSentry\dotNetFx40_Full_x86_x64.exe, reboot, then LogSentry\LogSentry.exe"
Say "Check : 7za t .pkg\payload.7z   /   7za l .pkg\payload.7z"
