# =====================================================================
# pack-zip.ps1 - 把 dist\ 里该发的文件打成绿色包
# ---------------------------------------------------------------------
# 只用 7-Zip 的命令行（7za.exe）压 zip，不生成任何 exe，不改任何二进制。
#
# 两种包：
#   轻量包（默认）  LogSentry.exe + LogParser.exe + queries.txt
#                    + 使用说明.txt + LogParser.chm（只认这一个 txt，避免带上验证产物）
#                   约 1.9 MB，Win10/11 自带 .NET 4.8，现场直接解压可用。
#   完整包（-WithDotNet）  再塞进 dist\NDP48-x86-x64-AllOS-ENU.exe
#                   约 120 MB，给 Win7 未打补丁 / 完全没有 .NET 4.5 的机器。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-zip.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-zip.ps1 -WithDotNet
#   powershell -NoProfile -ExecutionPolicy Bypass -File pack-zip.ps1 -OutputName LogSentry_v1.0_full.zip -WithDotNet
#
# 产物：
#   默认落在 .pkg\<OutputName>，解压后是一个 LogSentry\ 目录（不会把文件散到目标目录根）。
#
# 注意：
#   * 本脚本必须存成 UTF-8 with BOM。没有 BOM 时 Windows PowerShell 5.1
#     会按 GBK 读脚本，中文注释会吃掉后面的引号，报 Unexpected token。
#   * 只打包 dist\ 里存在的文件：没有的东西静默跳过，最后打印清单。
#   * 故意不带 config\user-queries.txt：那是使用者自己的检测项，属于他们的现场数据，
#     打进包里既会泄露别人的查询，也会让「解压即覆盖」丢掉本机已有的自定义项。
#     要迁移自定义项，自己拷那个文件（使用说明第五节「加法 B」有写）。
# =====================================================================

param(
    [string]$DistDir = "dist",
    [string]$OutputName = "LogSentry_v1.0.zip",
    [switch]$WithDotNet,
    [int]$CompressionLevel = 5
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say([string]$msg) { Write-Host $msg }

# ------------------------------------------------------------------ 找 7-Zip
function Find-7Zip {
    $candidates = @(
        (Join-Path $root ".tools\extra\7za.exe"),        # 仓库自带，优先
        (Join-Path $root ".tools\extra25\7za.exe"),
        (Join-Path ${env:ProgramFiles} "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe"),
        (Join-Path ${env:LOCALAPPDATA} "Programs\7-Zip\7z.exe")
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$sevenZip = Find-7Zip
if (-not $sevenZip) {
    Say "[!] 找不到 7-Zip。请把 7za.exe 放到 .tools\extra\ 或安装 7-Zip。"
    exit 1
}

# ------------------------------------------------------------------ 整理文件清单
$distPath = Join-Path $root $DistDir
if (-not (Test-Path $distPath)) { Say "[!] 目录不存在：$distPath"; exit 1 }

# 核心三项：少一个都不是能跑的包
$required = @("LogSentry.exe", "LogParser.exe", "queries.txt")
# 可选：手册与帮助文件，有就带
$optional = @("LogParser.chm")
# 只认发行手册这一个 txt：dist\ 里还躺着 _smoke.exe 写的 ui-results.txt / smoke.log 之类
# 的验证产物，用 *.txt 通配会把它们一起塞进包（2026-11-12 实测确实发生过）。
foreach ($f in @(Get-ChildItem $distPath -Filter "*.txt" -File)) {
    if ($f.Name -eq "使用说明.txt") { $optional += $f.Name }
}
# 上面那条会把 queries.txt 也收进来（它本来就是必需的），这里去重
$optional = $optional | Where-Object { $required -notcontains $_ }

$files = @()
foreach ($n in $required) {
    if (Test-Path (Join-Path $distPath $n)) { $files += $n }
    else { Say "[!] 缺少必需文件：$n"; exit 1 }
}
foreach ($n in ($optional | Select-Object -Unique)) {
    if (Test-Path (Join-Path $distPath $n)) { $files += $n }
    else { Say "[*] 跳过（不存在）：$n" }
}
if ($WithDotNet) {
    $ndp = Join-Path $distPath "NDP48-x86-x64-AllOS-ENU.exe"
    if (Test-Path $ndp) { $files += "NDP48-x86-x64-AllOS-ENU.exe" }
    else { Say "[!] 开了 -WithDotNet 但 dist 里没有 NDP48-x86-x64-AllOS-ENU.exe"; exit 1 }
}

# ------------------------------------------------------------------ 暂存
$stage = Join-Path $root ".pkg\zipstage\LogSentry"
if (Test-Path (Split-Path -Parent $stage)) { Remove-Item (Split-Path -Parent $stage) -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($n in $files) {
    Copy-Item (Join-Path $distPath $n) (Join-Path $stage $n) -Force
    $item = Get-Item (Join-Path $stage $n)
    Say ("    + " + $n + "  (" + [string]::Format("{0:N1}", ($item.Length / 1KB)) + " KB)")
}

# ------------------------------------------------------------------ 打包
$outDir = Join-Path $root ".pkg"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$outPath = Join-Path $outDir $OutputName
if (Test-Path $outPath) { Remove-Item $outPath -Force }

Push-Location (Split-Path -Parent $stage)
try {
    & $sevenZip a -tzip ("-mx=" + $CompressionLevel) $outPath "LogSentry" | Out-Null
} finally {
    Pop-Location
}

if (-not (Test-Path $outPath)) { Say "[!] 打包失败：$outPath 没生成"; exit 1 }

# ------------------------------------------------------------------ 校验与报告
& $sevenZip t $outPath | Out-Null
if ($LASTEXITCODE -ne 0) { Say "[!] 7za t 校验失败"; exit 1 }

$info = Get-Item $outPath
$sha = (Get-FileHash $outPath -Algorithm SHA256).Hash
Say ""
Say "[OK] 打包完成"
Say ("     路径   : " + $outPath)
Say ("     大小   : " + [string]::Format("{0:N2}", ($info.Length / 1MB)) + " MB (" + $info.Length + " 字节)")
Say ("     条目   : " + $files.Count + " 个文件")
Say ("     SHA256 : " + $sha)
Say ("     解压后 : LogSentry\  (" + ($files -join ", ") + ")")

# 暂存目录用完就删，避免 .pkg 里堆旧文件
Remove-Item (Split-Path -Parent $stage) -Recurse -Force
