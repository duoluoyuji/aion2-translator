using System.Diagnostics;
using System.Text;
using AionTranslator.Core.Game;

var pid = Environment.ProcessId;
var exe = Environment.ProcessPath ?? "";
using var h = Win32.OpenProcess(Win32.ProcessQueryInformation | Win32.ProcessVmRead, false, pid);
if (h.IsInvalid) { Console.WriteLine("open self failed"); return 1; }
if (!GameDiagnostics.TryGetMainModule(h, exe, out var baseAddr, out var size, out var err))
{ Console.WriteLine("module fail: " + err); return 1; }
Console.WriteLine($"base=0x{baseAddr:X} size={size}");

var mem = new ProcessMemory(h);
var needle = Encoding.ASCII.GetBytes("DumpTest");
var mask = Enumerable.Repeat((byte)1, needle.Length).ToArray();
var hit = mem.FindPattern(baseAddr, baseAddr + size, needle, mask);
Console.WriteLine(hit is null ? "FindPattern: MISS" : $"FindPattern: HIT 0x{hit.Value:X} (rva 0x{hit.Value - baseAddr:X})");

// 再测一次跨长度读取（故意读一个很可能跨区域的大块，看是否不死锁）
var buf = new byte[1024];
Console.WriteLine("Read 1KB at base: " + mem.Read(baseAddr, buf));
return 0;
