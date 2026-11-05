$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'bin\Release\net10.0-windows\ChatScan.exe'
$proc = Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $proc) { Write-Host 'no AION2 process'; pause; exit 1 }
Write-Host "target pid=$($proc.Id)"

Write-Host ''
Write-Host '=== 扫描输入框的占位提示文字（请输入对话）==='
& $exe $proc.Id scan '请输入对话' (Join-Path $root 'out\hint.txt')

Write-Host ''
Write-Host '=== dump 这些命中 ==='
& $exe $proc.Id dumpfile (Join-Path $root 'out\hint.txt') (Join-Path $root 'out\hint-dump.txt')

Write-Host ''
Write-Host 'done: out\hint.txt / out\hint-dump.txt'
pause
