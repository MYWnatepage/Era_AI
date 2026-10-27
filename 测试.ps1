param([switch]$Engine, [switch]$Scenario)
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

if ($Scenario -and !$Engine) {
    $scenarioScripts = @('test_scenario_isolation.py', 'test_food_rule.py', 'test_rain_room_cards.py', 'test_inventory_engine.py', 'test_character_status_engine.py', 'test_inventory_review.py', 'test_actions_engine.py', 'test_scenario_load.py', 'test_history_save_sync.py')
    python --version
    if ($LASTEXITCODE -ne 0) { throw '剧本测试需要可用的 Python。' }
    foreach ($name in $scenarioScripts) {
        python (Join-Path $PSScriptRoot "测试/$name")
        if ($LASTEXITCODE -ne 0) { throw "剧本测试失败：$name" }
    }
}
