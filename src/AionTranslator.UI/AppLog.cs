using System.IO;

namespace AionTranslator.UI;

internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();

    internal static void Remember(string message)
    {
        lock (Gate)
        {
            Recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + message);
            while (Recent.Count > 300) Recent.Dequeue();
        }
    }

    internal static string Dump()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Recent);
        }
    }

    private const long MaxBytes = 1_500_000; // 超过 1.5MB 就裁到最近 800 行

    internal static void Write(string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + " " + message + Environment.NewLine;
        Remember(message);
        foreach (var path in new[]
                 {
                     Path.Combine(AppPaths.DataDir, "app.log"),
                     Path.Combine(AppContext.BaseDirectory, "app.log"),
                 })
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    Trim(path);
                    File.AppendAllText(path, line);
                }
                return;
            }
            catch
            {
                // 换下一个路径
            }
        }
    }

    /// <summary>日志无限长会吃掉几百 MB，超过阈值就只留最近的记录。</summary>
    private static void Trim(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var lines = File.ReadAllLines(path);
            var keep = lines.Length > 800 ? lines[^800..] : lines;
            File.WriteAllLines(path, keep);
        }
        catch
        {
            // 裁剪失败不影响继续写
        }
    }
}