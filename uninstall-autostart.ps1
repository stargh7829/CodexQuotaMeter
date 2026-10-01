#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$executable = Join-Path $env:USERPROFILE 'AppTools\CodexQuotaMeter\CodexQuotaMeter.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    $executable = Join-Path $PSScriptRoot 'CodexQuotaMeter.exe'
}
. (Join-Path $PSScriptRoot 'launch-detached.ps1')
Start-QuotaDetached -Executable $executable -Arguments '--disable-startup'
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'CodexQuotaMeter' -ErrorAction SilentlyContinue
Write-Host '已取消 Codex 额度悬浮条的开机启动。'
