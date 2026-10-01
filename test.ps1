param([switch]$Live, [string]$PreviewDirectory)
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testOutput = Join-Path ([IO.Path]::GetTempPath()) ('CodexQuotaRegression-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    & $compiler /nologo /codepage:65001 /target:exe /main:RegressionTests `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll `
        "/out:$testOutput" (Join-Path $projectDir 'CodexUsageRemaining.cs') (Join-Path $projectDir 'RegressionTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败。' }
    if ($Live) { & $testOutput --live }
    elseif ($PreviewDirectory) { & $testOutput --preview $PreviewDirectory }
    else { & $testOutput }
    if ($LASTEXITCODE -ne 0) { throw "测试未通过，退出码 $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $testOutput -ErrorAction SilentlyContinue
}
