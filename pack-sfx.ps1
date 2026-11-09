#requires -version 3.0
# LogSentry single-file packer (7-Zip SFX).
#
# Output: dist\LogSentry_Portable.exe  -- one file, double-click extracts to %TEMP%
# and launches LogSentry.exe (which self-elevates via its requireAdministrator manifest).
#
# Requirements:
#   - 7z.exe  (any recent 7-Zip build; searched in the usual places)
#   - a 7z SFX module: 7z.sfx (GUI, progress window) or 7zS.sfx (silent)
#     Take it from the official 7-Zip package (7zXXXX-x64.exe or 7zXXXX-extra.7z)
#     and drop it in .tools\sfx\7z.sfx, or pass -SfxModule.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-sfx.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-sfx.ps1 -NoSelfDelete

param(
    [string]$DistDir = "dist",
    [string]$OutputName = "LogSentry_Portable.exe",
    [string]$SfxModule = "",
    [string]$SevenZip = "",
    [switch]$NoSelfDelete,
    [switch]$KeepTemp
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

function Say($m) { Write-Host $m }

function Find-7Zip {
    if ($SevenZip -and (Test-Path $SevenZip)) { return $SevenZip }
    $cands = @(
        (Join-Path ${env:ProgramFiles} "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe"),
        "C:\ProgramData\chocolatey\tools\7z.exe"
    )
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return $c } }
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "7z.exe not found. Install 7-Zip or pass -SevenZip <path>."
}

function Find-Sfx {
    if ($SfxModule -and (Test-Path $SfxModule)) { return (Resolve-Path $SfxModule).Path }
    $cands = @(
        (Join-Path $root ".tools\sfx\7z.sfx"),
        (Join-Path $root ".tools\sfx\7zS.sfx"),
        (Join-Path ${env:ProgramFiles} "7-Zip\7z.sfx"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.sfx")
    )
    foreach ($c in $cands) { if ($c -and (Test-Path $c)) { return $c } }
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
# (%TEMP%\7zSxxxx.tmp\LogSentry\...) instead of scattering five loose files in
# the temp root. sfx-config.txt then points Directory at that folder.
$stageRoot = Join-Path $root ".pkg\stage"
$stage = Join-Path $stageRoot "LogSentry"
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# ASCII-only file names here: this script must stay readable whether the host
# reads it as UTF-8 or as ANSI, so the Chinese-named manual is picked up by
# wildcard instead of being hard-coded.
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

# ---- archive
$archive = Join-Path $root ".pkg\payload.7z"
if (Test-Path $archive) { Remove-Item $archive -Force }
Push-Location $stageRoot
try {
    & $sz a -t7z -mx=9 -mhc=on "-w$root\.pkg" $archive "LogSentry" | Out-Null
} finally { Pop-Location }
$archiveSize = (Get-Item $archive).Length
Say ("[*] archive: " + $archive + " (" + $archiveSize + " bytes)")

# ---- SFX config (UTF-8 WITHOUT BOM - mandatory, the stub parses the first line literally)
# The Chinese values live in sfx-config.txt and are read with -Encoding UTF8 on purpose:
# this .ps1 may be decoded as ANSI by the host, which would mangle non-ASCII literals
# written directly in here (that is exactly how the title turned into mojibake once).
$cfgTemplate = Join-Path $root "sfx-config.txt"
if (-not (Test-Path $cfgTemplate)) { throw "sfx-config.txt not found: $cfgTemplate" }
$body = @(Get-Content -LiteralPath $cfgTemplate -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 -and -not $_.TrimStart().StartsWith(";") })
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

$sizeKB = [math]::Round((Get-Item $out).Length / 1KB, 1)
Say ("[OK] " + $out + "  (" + $sizeKB + " KB)")
Say ""
Say "Payload layout: everything extracts into a folder, not the temp root:"
Say "  %TEMP%\7zSxxxxx.tmp\LogSentry\LogSentry.exe"
Say "  (sfx-config.txt: Directory=.\LogSentry\ + RunProgram=LogSentry.exe)"
Say ""
Say "Verify: copy the exe to another machine, double-click -> UAC prompt -> the app"
Say "starts from that folder. The temp folder is removed when the app exits."
