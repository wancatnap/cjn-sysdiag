using System.IO;
using System.Text.Json;

namespace SysDiag.Services;

/// <summary>
/// 轻量本地配置：记录最近分析过的目录，让清理清单也能覆盖非标准路径（如装在 D 盘的游戏日志）。
/// 存放在 LocalAppData，避免程序被放到只读目录时写入失败。
/// </summary>
public static class AppSettings
{
    private sealed class Model
    {
        public List<string> RecentFolders { get; set; } = new();
    }

    public static List<string> LoadRecentFolders()
    {
        try
        {
            MigrateLegacy();
            if (!File.Exists(AppPaths.SettingsFile)) return new List<string>();

            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(AppPaths.SettingsFile));
            var dirs = model?.RecentFolders?
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            return dirs ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void AddRecentFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            MigrateLegacy();
            Directory.CreateDirectory(AppPaths.DataDir);

            var model = new Model();
            if (File.Exists(AppPaths.SettingsFile))
            {
                try
                {
                    model = JsonSerializer.Deserialize<Model>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Model();
                }
                catch { model = new Model(); }
            }

            var full = Path.GetFullPath(folder).TrimEnd('\\');
            model.RecentFolders.RemoveAll(p =>
                string.Equals(p?.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
            model.RecentFolders.Insert(0, full);

            if (model.RecentFolders.Count > 8)
                model.RecentFolders = model.RecentFolders.Take(8).ToList();

            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch
        {
            // 无权限或磁盘异常时静默失败，不影响主流程
        }
    }

    /// <summary>旧版本把 settings.json 放在 exe 同目录，首次运行时迁移一次。</summary>
    private static void MigrateLegacy()
    {
        try
        {
            var legacy = Path.Combine(AppPaths.Root, "settings.json");
            if (File.Exists(AppPaths.SettingsFile) || !File.Exists(legacy)) return;

            Directory.CreateDirectory(AppPaths.DataDir);
            File.Copy(legacy, AppPaths.SettingsFile, true);
        }
        catch { }
    }
}
