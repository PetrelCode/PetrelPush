<#
.SYNOPSIS
    生成 PetrelPush 的 MSI 安装包。

.DESCRIPTION
    1) 以 self-contained（win-x64）方式发布托盘应用，用户无需安装 .NET 运行时；
    2) 用 WiX Toolset (Sdk 5) 把发布目录打包为 MSI；
    3) 输出到 artifacts\PetrelPushSetup-<版本>-x64.msi。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -Version 0.1.1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.3.0'
)

$ErrorActionPreference = 'Stop'

# 安装包仅支持 x64（Package.wxs 使用 ProgramFiles64Folder，平台在 wixproj 中固定为 x64）
if ($Runtime -ne 'win-x64') {
    throw "当前安装包仅支持 win-x64（收到：$Runtime）"
}

$arch = 'x64'

# 不依赖调用方终端的 PATH：优先取命令，其次取默认安装位置
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $candidate) {
        $dotnet = $candidate
    }
}

if (-not $dotnet) {
    throw '未找到 dotnet 命令。请先安装 .NET 8 SDK，或把 C:\Program Files\dotnet 加入 PATH。'
}

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'
$msiSource = Join-Path $root "installer\bin\x64\$Configuration\PetrelPushSetup.msi"
$msiTarget = Join-Path $root "artifacts\PetrelPushSetup-$Version-$arch.msi"

Write-Host '[1/3] 发布 self-contained 应用…' -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

& $dotnet publish (Join-Path $root 'PetrelPush.App\PetrelPush.App.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -o $publishDir `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

Write-Host '[2/3] 编译 MSI…' -ForegroundColor Cyan
& $dotnet build (Join-Path $PSScriptRoot 'PetrelPush.Setup.wixproj') `
    -c $Configuration `
    -p:ProductVersion=$Version `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "WiX 编译失败（退出码 $LASTEXITCODE）"
}

Write-Host '[3/3] 复制安装包…' -ForegroundColor Cyan
$artifacts = Split-Path -Parent $msiTarget
if (-not (Test-Path $artifacts)) {
    New-Item -ItemType Directory -Path $artifacts | Out-Null
}

Copy-Item $msiSource $msiTarget -Force
$size = [math]::Round((Get-Item $msiTarget).Length / 1MB, 1)

Write-Host "完成：$msiTarget（$size MB）" -ForegroundColor Green
