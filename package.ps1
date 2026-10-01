#requires -Version 7.0
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$distRoot = Join-Path $PSScriptRoot 'dist'
$staging = Join-Path $distRoot ('staging-' + [guid]::NewGuid().ToString('N'))
$packageDir = Join-Path $staging 'CodexQuotaMeter'
$zipPath = Join-Path $distRoot 'CodexQuotaMeter-Windows-v1.1.4.zip'
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
try {
    $files = @('CodexQuotaMeter.exe', 'setup.ps1', 'setup.bat', '一键配置.bat', 'launch-detached.ps1',
        'uninstall-autostart.ps1', 'uninstall-autostart.bat', '取消自动启动.bat',
        'README.md', '使用说明.md', 'LICENSE')
    foreach ($file in $files) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packageDir
    }
    Compress-Archive -LiteralPath $packageDir -DestinationPath $zipPath -CompressionLevel Optimal -Force
    (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' +
        [IO.Path]::GetFileName($zipPath) | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii
    Write-Output "已生成：$zipPath"
} finally {
    # Verify the exact absolute staging path before any recursive deletion.
    $resolvedRoot = [IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if (-not $resolvedStaging.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw '暂存目录超出打包目录，停止清理。'
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}
