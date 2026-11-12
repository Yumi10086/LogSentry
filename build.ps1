#requires -version 3.0
# LogSentry build script (no VS / no .NET SDK required).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-PackSfx]
param(
    [switch]$PackSfx,
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root
$culture = [System.Globalization.CultureInfo]::InvariantCulture
[System.Threading.Thread]::CurrentThread.CurrentCulture = $culture

function Say($msg) { Write-Host $msg }

function Find-Csc {
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "csc.exe not found. Need .NET Framework 4.x (Windows 7 SP1 or later)."
}

function Find-RefDir {
    $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $pf = [Environment]::GetFolderPath('ProgramFiles')
    $sub = "Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
    foreach ($p in @((Join-Path $pf86 $sub), (Join-Path $pf $sub))) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

$csc = Find-Csc
Say ("[*] compiler : " + $csc)

$refDir = Find-RefDir
$fwDir = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path (Join-Path $fwDir "System.Windows.Forms.dll"))) {
    $fwDir = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
}
$asmBase = if ($refDir) { $refDir } else { $fwDir }
Say ("[*] refs     : " + $asmBase)

$refs = @()
foreach ($asm in @("System.dll", "System.Core.dll", "System.Windows.Forms.dll", "System.Drawing.dll", "System.Data.dll", "System.Xml.dll", "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll")) {
    $full = Join-Path $asmBase $asm
    if (Test-Path $full) { $refs += "/r:$full" }
    else {
        $alt = Join-Path $fwDir $asm
        if (Test-Path $alt) { $refs += "/r:$alt" }
        else { Say ("[!] missing reference: " + $full) }
    }
}

$srcDir = Join-Path $root "src"
$manifest = Join-Path $srcDir "app.manifest"
# 主程序源码里排除 SelfTest.cs（它有自己的 console Main，单独编成 _selftest.exe）
$sources = @(Get-ChildItem (Join-Path $srcDir "*.cs") | Where-Object { $_.Name -ne "SelfTest.cs" } | ForEach-Object { $_.FullName })

$out = Join-Path $root $OutDir
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }
$exe = Join-Path $out "LogSentry.exe"

$common = @("/nologo", "/utf8output", "/codepage:65001", "/warn:4", "/optimize+", "/target:winexe", "/platform:anycpu")
$buildArgs = $common + @("/win32manifest:$manifest", "/out:$exe") + $refs + $sources

Say ("[*] building : " + $exe)
& $csc @buildArgs
if ($LASTEXITCODE -ne 0) { throw ("compile failed, exit code " + $LASTEXITCODE) }

$size = (Get-Item $exe).Length
Say ("[OK] LogSentry.exe built, " + [string]::Format($culture, "{0:N1}", ($size / 1KB)) + " KB")

# console self-test binary (verifies engine + config, does not need admin for System log items)
$testExe = Join-Path $out "_selftest.exe"
$testSources = @(Get-ChildItem (Join-Path $srcDir "*.cs") | ForEach-Object { $_.FullName })
$testArgs = @("/nologo", "/utf8output", "/codepage:65001", "/define:SELFTEST", "/target:exe", "/out:$testExe") + $refs + $testSources
& $csc @testArgs
if ($LASTEXITCODE -ne 0) { Say "[!] selftest build failed (main exe is fine)" }
else { Say ("[OK] selftest : " + $testExe) }

# console UI smoke harness (drives the real form: --smoke), so automated checks never run stale binaries
$smokeExe = Join-Path $out "_smoke.exe"
$smokeArgs = @("/nologo", "/utf8output", "/codepage:65001", "/target:exe", "/out:$smokeExe") + $refs + $sources
& $csc @smokeArgs
if ($LASTEXITCODE -ne 0) { Say "[!] smoke build failed (main exe is fine)" }
else { Say ("[OK] smoke    : " + $smokeExe) }

if (-not (Test-Path (Join-Path $out "LogParser.exe"))) {
    Say "[!] LogParser.exe not found in dist - app starts but cannot query."
    Say "    Copy LogParser.exe (Microsoft Log Parser 2.2) into that folder."
}

if ($PackSfx) {
    $pack = Join-Path $root "pack-sfx.ps1"
    if (Test-Path $pack) { & powershell -NoProfile -ExecutionPolicy Bypass -File $pack }
    else { Say "[!] pack-sfx.ps1 missing, skip single-file packaging" }
}
Say "done."
