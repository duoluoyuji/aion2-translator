$ErrorActionPreference = 'Continue'

$sig = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public const uint WM_KEYDOWN=0x0100, WM_KEYUP=0x0101, WM_CHAR=0x0102;
}
"@
Add-Type -TypeDefinition $sig

$proc = Get-Process AION2 | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (-not $proc) { Write-Host 'no AION2 process'; pause; exit 1 }
Write-Host "target pid=$($proc.Id)"

$found = @()
$cb = [W+EnumProc]{
  param($h, $l)
  $p = 0
  [void][W]::GetWindowThreadProcessId($h, [ref]$p)
  if ($p -eq $proc.Id -and [W]::IsWindowVisible($h)) {
    $cls = New-Object System.Text.StringBuilder 256
    [void][W]::GetClassName($h, $cls, 256)
    $txt = New-Object System.Text.StringBuilder 256
    [void][W]::GetWindowText($h, $txt, 256)
    $script:found += [pscustomobject]@{ H=$h; Class=$cls.ToString(); Title=$txt.ToString() }
  }
  return $true
}
[void][W]::EnumWindows($cb, [IntPtr]::Zero)
$found | ForEach-Object { "hwnd=0x{0:X} class={1} title={2}" -f $_.H.ToInt64(), $_.Class, $_.Title }

$target = ($found | Where-Object { $_.Class -like '*Unreal*' } | Select-Object -First 1)
if (-not $target) { $target = ($found | Where-Object { $_.Title -ne '' } | Select-Object -First 1) }
if (-not $target) { Write-Host 'no game window'; pause; exit 1 }
Write-Host "using hwnd=0x$('{0:X}' -f $target.H.ToInt64())"

Write-Host ''
Write-Host '5 秒后开始投递按键：请在这 5 秒内切回游戏，并让聊天输入框处于打开状态'
for ($i = 5; $i -gt 0; $i--) { Write-Host "  $i"; Start-Sleep -Seconds 1 }

function Key([int]$vk) {
  [void][W]::PostMessage($target.H, [W]::WM_KEYDOWN, [IntPtr]$vk, [IntPtr]0x001C0001)
  Start-Sleep -Milliseconds 30
  [void][W]::PostMessage($target.H, [W]::WM_KEYUP, [IntPtr]$vk, [IntPtr]0xC01C0001)
  Start-Sleep -Milliseconds 60
}

$text = 'anyone selling cheap potions'
Write-Host "投递文本: $text"
foreach ($ch in $text.ToCharArray()) {
  [void][W]::PostMessage($target.H, [W]::WM_CHAR, [IntPtr][int]$ch, [IntPtr]1)
  Start-Sleep -Milliseconds 40
}
Start-Sleep -Milliseconds 300
Write-Host '按回车发送'
Key 0x0D

Write-Host ''
Write-Host '看游戏里有没有出现这句话。结果告诉我。'
pause
