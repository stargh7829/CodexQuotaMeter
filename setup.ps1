#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
# USERPROFILE is stable when invoked from a Microsoft Store/MSIX application.
$installDir = Join-Path $env:USERPROFILE 'AppTools\CodexQuotaMeter'
$sourceExe = Join-Path $sourceDir 'CodexQuotaMeter.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { throw '程序不存在，请先完整解压下载包。' }

# Upgrade this user's existing meter, then install to a stable location.
$running = @(Get-Process -Name 'CodexQuotaMeter' -ErrorAction SilentlyContinue)
foreach ($process in $running) {
    $null = $process.Handle
    Stop-Process -InputObject $process -ErrorAction Stop
    $process.WaitForExit(5000) | Out-Null
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$files = @('CodexQuotaMeter.exe', 'setup.ps1', 'setup.bat', 'launch-detached.ps1', 'uninstall-autostart.ps1',
    'uninstall-autostart.bat', 'README.md', '使用说明.md', 'LICENSE')
foreach ($file in $files) {
    $source = [IO.Path]::GetFullPath((Join-Path $sourceDir $file))
    $target = [IO.Path]::GetFullPath((Join-Path $installDir $file))
    if ($source -ne $target -and (Test-Path -LiteralPath $source)) {
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
}
$executable = Join-Path $installDir 'CodexQuotaMeter.exe'
# Remove an earlier startup entry in this invocation's registry view.
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'CodexQuotaMeter' -ErrorAction SilentlyContinue
. (Join-Path $sourceDir 'launch-detached.ps1')
Start-QuotaDetached -Executable $executable -Arguments '--enable-startup'
Write-Host '已安装并启用随 Windows 启动，无需管理员权限。'
Write-Host "安装位置：$installDir"
Write-Host '账号与额度每 1 分钟刷新；关闭或重启 Codex 后自动重新显示。'
