$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'bin\Release\net10.0-windows\ChatScan.exe'
$proc = Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $proc) { Write-Host 'no AION2 process'; pause; exit 1 }
Write-Host "target pid=$($proc.Id)"
$needle = 'where the fishing vendor is'

Write-Host ''
Write-Host '=== 1/3 扫描（输入框里应当还有那句）==='
& $exe $proc.Id scan $needle (Join-Path $root 'out\live1.txt')

Write-Host ''
Write-Host '=== 2/3 反向指针扫描（找谁指着这些字符串）==='
& $exe $proc.Id ptrscan (Join-Path $root 'out\live1.txt') (Join-Path $root 'out\ptr-live.txt')

Write-Host ''
Write-Host '=== 3/3 请回到游戏：Ctrl+A 然后 Delete 清空聊天输入框（不要发送），完成后回来按任意键 ==='
pause | Out-Null

Write-Host '清空后再扫一次…'
& $exe $proc.Id scan $needle (Join-Path $root 'out\live2.txt')

Write-Host ''
Write-Host 'done: out\live1.txt / out\ptr-live.txt / out\live2.txt'
pause
