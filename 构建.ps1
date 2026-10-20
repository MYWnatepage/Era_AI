$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot '源码/Emuera/Emuera.csproj'
$outputPath = Join-Path $PSScriptRoot '游戏'
$sourceMap = "$PSScriptRoot=/src"
dotnet publish $projectPath -c Release-NAudio -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$sourceMap" -o $outputPath
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Write-Output '构建完成，进入游戏文件夹运行 EraAI.exe。'
