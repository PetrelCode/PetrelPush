# PetrelPush 卸载清理脚本（由 MSI 自定义动作以 SYSTEM 权限调用）
# 清理内容：
#   1) 所有本地用户的 %APPDATA%\PetrelPush（config.json / secrets.bin / state.db / logs）
#   2) 所有本地用户的 %LOCALAPPDATA%\PetrelPush（如有）
#   3) %ProgramData%\PetrelPush（如有）
#   4) 各用户 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的 PetrelPush 自启项
$ErrorActionPreference = 'SilentlyContinue'
$productName = 'PetrelPush'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'

function Remove-RunValue([string]$KeyPath) {
    if (Test-Path -LiteralPath $KeyPath) {
        Remove-ItemProperty -LiteralPath $KeyPath -Name $productName -Force -ErrorAction SilentlyContinue
    }
}

# 1) 结束仍在运行的实例，避免 state.db / 日志文件被占用
Get-Process -Name $productName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# 2) 收集所有用户 Profile 目录：注册表 ProfileList（含迁移到其他磁盘的 Profile）+ 系统 Users 目录兜底
$profileDirs = New-Object 'System.Collections.Generic.List[string]'
$profileListKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
Get-ChildItem -LiteralPath $profileListKey -ErrorAction SilentlyContinue | ForEach-Object {
    $image = (Get-ItemProperty -LiteralPath $_.PSPath -Name 'ProfileImagePath' -ErrorAction SilentlyContinue).ProfileImagePath
    if ($image) { $profileDirs.Add($image) }
}

$usersRoot = Join-Path $env:SystemDrive 'Users'
if (Test-Path -LiteralPath $usersRoot) {
    Get-ChildItem -LiteralPath $usersRoot -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $profileDirs.Add($_.FullName)
    }
}

# 3) 删除每个用户的配置目录
$profileDirs | Select-Object -Unique | ForEach-Object {
    $profile = $_
    if (-not (Test-Path -LiteralPath $profile)) { return }

    foreach ($relative in @('AppData\Roaming\PetrelPush', 'AppData\Local\PetrelPush')) {
        $path = Join-Path $profile $relative
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# 4) 删除机器级数据（若存在）
$programData = Join-Path $env:ProgramData $productName
if (Test-Path -LiteralPath $programData) {
    Remove-Item -LiteralPath $programData -Recurse -Force -ErrorAction SilentlyContinue
}

# 5) 已加载的用户注册表配置单元（登录中的用户）直接清理自启项
Get-ChildItem -Path 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-RunValue -KeyPath ('Registry::' + $_.Name + '\' + $runKey)
}

# 6) 未登录用户的配置单元未加载，临时挂载 NTUSER.DAT 后清理自启项
$mountIndex = 0
$profileDirs | Select-Object -Unique | ForEach-Object {
    $hive = Join-Path $_ 'NTUSER.DAT'
    if (-not (Test-Path -LiteralPath $hive)) { return }

    $mount = 'HKU\PetrelPushPurge' + $mountIndex
    $mountIndex++

    & reg.exe load $mount $hive 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        try {
            Remove-RunValue -KeyPath ('Registry::' + $mount + '\' + $runKey)
        }
        finally {
            # 卸载配置单元：句柄释放可能有延迟，失败时重试，避免残留临时挂载点
            for ($i = 0; $i -lt 3; $i++) {
                [gc]::Collect()
                [gc]::WaitForPendingFinalizers()
                & reg.exe unload $mount 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) { break }
                Start-Sleep -Milliseconds 300
            }
        }
    }
}

exit 0
