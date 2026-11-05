$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'bin\Release\net10.0-windows\ChatScan.exe'
$proc = Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $proc) { Write-Host 'no AION2 process'; pause; exit 1 }
Write-Host "target pid=$($proc.Id)"

Write-Host '--- 第二次扫描（应能搜到聊天记录里的那条）---'
& $exe $proc.Id scan 'where the fishing vendor is' (Join-Path $root 'out\after.txt')

Write-Host '--- dump 第一次的 43 个候选 ---'
& $exe $proc.Id dumpfile (Join-Path $root 'out\before.txt') (Join-Path $root 'out\dump-before.txt')

Write-Host ''
Write-Host 'done: out\after.txt / out\dump-before.txt'
pause
