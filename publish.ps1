<#
.SYNOPSIS
    一键发布：本地构建 → 打 tag → 推送 → GitHub Actions 自动出 Release。

.DESCRIPTION
    在仓库根目录执行。GitHub Actions 会自动构建、生成更新清单并发布 Release，
    软件的「检查更新」就能收到新版。

.PARAMETER Version
    版本号，如 0.33。不填则读取 ClassManager.App.csproj 里的 <Version>。

.PARAMETER SkipBuild
    跳过本地构建（只推送已构建好的版本）。

.EXAMPLE
    .\发布.ps1
    .\发布.ps1 -Version 0.34
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Git {
    param([string[]]$Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') 失败（退出码 $LASTEXITCODE）"
    }
}

Write-Host '==> 检查工作区状态' -ForegroundColor Cyan
$dirty = git status --porcelain
if ($dirty) {
    Write-Host '工作区有未提交的改动：' -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    Write-Host '先提交这些改动再发布，或用 git stash 暂存。' -ForegroundColor Yellow
    exit 1
}

if (-not $Version) {
    $csproj = Get-Content 'ClassManager.App\ClassManager.App.csproj' -Raw
    $m = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
    if (-not $m.Success) {
        throw '读不到 ClassManager.App.csproj 里的 <Version>'
    }
    $Version = $m.Groups[1].Value.Trim()
}

# 把 csproj 的版本同步成目标版本。
# 不然本地构建出来的 exe 内部版本会和 tag 对不上：
# 程序会认为「线上版本比自己新」→ 反复提示更新，装了也停不下来。
$csprojPath = 'ClassManager.App\ClassManager.App.csproj'
$csprojText = Get-Content $csprojPath -Raw
$updated = [regex]::Replace($csprojText, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
$updated = [regex]::Replace($updated, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>")
$updated = [regex]::Replace($updated, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>")

if ($updated -ne $csprojText) {
    [System.IO.File]::WriteAllText((Resolve-Path $csprojPath), $updated, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    已把 csproj 版本同步为 $Version" -ForegroundColor DarkGray
}

Write-Host "==> 版本号：$Version" -ForegroundColor Cyan

$tag = "v$Version"
if (git tag -l $tag) {
    Write-Host "tag $tag 已存在。先删掉再重发：git tag -d $tag; git push origin :$tag" -ForegroundColor Yellow
    exit 1
}

if (-not $SkipBuild) {
    Write-Host '==> 本地构建（自包含单文件）' -ForegroundColor Cyan
    & dotnet publish 'ClassManager.App\ClassManager.App.csproj' `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=none `
        -p:SatelliteResourceLanguages=zh-Hans `
        -o publish
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败（退出码 $LASTEXITCODE）"
    }
    Write-Host "    产物：publish\ClassManager.App.exe" -ForegroundColor DarkGray
}

Write-Host '==> 拉取远端最新' -ForegroundColor Cyan
Invoke-Git @('pull', '--rebase', 'origin', 'main')

Write-Host "==> 打 tag 并推送：$tag" -ForegroundColor Cyan
Invoke-Git @('tag', $tag)
Invoke-Git @('push', 'origin', $tag)

Write-Host ''
Write-Host "已推送 $tag，GitHub Actions 正在自动发布（约 2 分钟）。" -ForegroundColor Green
Write-Host "看进度：https://github.com/KEY-MAKE/ClassManger/actions" -ForegroundColor Green
Write-Host "发布完的 Release：https://github.com/KEY-MAKE/ClassManger/releases/latest" -ForegroundColor Green
Write-Host ''
Write-Host '提醒：软件里的更新源填这个地址（永远指向最新 Release）：' -ForegroundColor Cyan
Write-Host '  https://github.com/KEY-MAKE/ClassManger/releases/latest/download/version.json' -ForegroundColor Cyan
