using System.IO;
using AionTranslator.Core;

namespace AionTranslator.UI;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AION2实时翻译");

    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>可能的配置文件位置，按优先级：用户漫游目录 → 程序目录 → 本地应用数据。</summary>
    public static IEnumerable<string> SettingsCandidates()
    {
        yield return Path.Combine(DataDir, "settings.json");
        yield return Path.Combine(ExeDir, "settings.user.json");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AION2实时翻译", "settings.json");
    }

    public static string SettingsFile => SettingsCandidates().First();

    public static Settings LoadSettings(out string usedPath)
    {
        EnsureDataFolder();
        // 多个位置都有配置时，用最新改过的那个
        var existing = SettingsCandidates()
            .Where(File.Exists)
            .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
            .ToList();
        foreach (var candidate in existing)
        {
            usedPath = candidate;
            try
            {
                return Settings.Load(candidate);
            }
            catch (Exception ex)
            {
                AppLog.Write("读取配置失败(" + candidate + "): " + ex.Message);
            }
        }
        usedPath = SettingsFile;
        return Settings.Load(usedPath);
    }

    /// <summary>保存到第一个能写的路径，返回实际写入的位置；全都写不了返回 null。</summary>
    public static string? SaveSettings(Settings settings, string preferred)
    {
        var order = new List<string> { preferred };
        order.AddRange(SettingsCandidates().Where(p => !string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase)));
        foreach (var path in order)
        {
            try
            {
                settings.Save(path);
                return path;
            }
            catch (Exception ex)
            {
                AppLog.Write("保存配置失败(" + path + "): " + ex.Message);
            }
        }
        return null;
    }

    public static void EnsureDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var promptDir = Path.Combine(DataDir, "prompt");
            Directory.CreateDirectory(promptDir);
            var source = Path.Combine(ExeDir, "prompt", "system-prompt.txt");
            var target = Path.Combine(promptDir, "system-prompt.txt");
            if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
        }
        catch (Exception ex)
        {
            AppLog.Write("准备数据目录失败: " + ex.Message);
        }
    }
}