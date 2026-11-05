$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$exe  = Join-Path $root 'bin\Release\net10.0-windows\ChatScan.exe'
$proc = Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $proc) { Write-Host 'no AION2 process'; pause; exit 1 }
Write-Host "target pid=$($proc.Id)"
Write-Host '=== 转储游戏内存镜像（约 400MB，几十秒）==='
& $exe $proc.Id dumpimage (Join-Path $root 'out\image.bin') (Join-Path $root 'out\dumpimage.txt')
Write-Host ''
Write-Host 'done: out\image.bin'
pause