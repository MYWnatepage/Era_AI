param([switch]$Engine)
$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot '测试/NativeAI.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '核心测试失败。' }
if ($Engine) {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot '游戏/EraAI.exe'))) { throw '请先运行构建.ps1。' }
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '测试') -Filter 'test_*.py' | Where-Object Name -ne 'test_support.py') {
        python $script.FullName
        if ($LASTEXITCODE -ne 0) { throw "引擎测试失败：$($script.Name)" }
    }
}
