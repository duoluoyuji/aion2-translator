$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'bin\Release\net10.0-windows\ChatScan.exe'
$proc = Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $proc) { Write-Host 'no AION2 process'; pause; exit 1 }
Write-Host "target pid=$($proc.Id)"

Write-Host ''
Write-Host '=== 原地等长改写 6 个候选字段（不动指针/长度/容量）==='
& $exe $proc.Id overwrite (Join-Path $root 'out\candidates.txt') 'fish vendor moved to town' (Join-Path $root 'out\write-test.txt')

Write-Host ''
Write-Host '=== 回游戏看聊天输入框：那行字是不是变成了 fish vendor moved to town ？（不要发送）==='
Write-Host '看完了回来说结果。'
Write-Host ''
Write-Host 'done: out\write-test.txt'
pause
