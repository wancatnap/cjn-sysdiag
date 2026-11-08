using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 一键优化 / 一键回滚。
/// 先把要改的注册表原值整体备份成 JSON，再逐项执行；回滚时按备份写回。
/// 只有「改了注册表」的动作参与回滚，清理缓存这类不可逆动作单独让用户勾选。
/// </summary>
public static class GameOptimizeService
{
    private const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string CompatKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string MouseKey = @"Control Panel\Mouse";

    public static string BackupFile => Path.Combine(AppPaths.DataDir, "game-optimize-backup.json");

    /// <summary>备份内容：每项都记「原来有没有值、值是什么」。</summary>
    public sealed class Backup
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string ExePath { get; set; } = "";
        public string GpuPrefRaw { get; set; } = "";
        public bool GpuPrefExisted { get; set; }
        public string CompatRaw { get; set; } = "";
        public bool CompatExisted { get; set; }
        public string MouseSpeed { get; set; } = "";
        public string MouseThreshold1 { get; set; } = "";
        public string MouseThreshold2 { get; set; } = "";
        public bool MouseExisted { get; set; }
    }

    public static Backup LoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile))
                return JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupFile)) ?? new Backup();
        }
        catch { }
        return null;
    }

    public static bool HasBackup => LoadBackup() != null;

    public static string BackupTimeText
    {
        get
        {
            var b = LoadBackup();
            return b == null ? "还没有备份" : "备份时间：" + b.Time.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>只做备份，不改任何设置。</summary>
    public static (bool Ok, string Message) CreateBackup(string exePath)
    {
        try
        {
            var backup = new Backup { ExePath = exePath };

            using (var key = Registry.CurrentUser.OpenSubKey(GpuPrefKey))
            {
                var raw = key?.GetValue(exePath) as string;
                backup.GpuPrefExisted = raw != null;
                backup.GpuPrefRaw = raw ?? "";
            }

            using (var key = Registry.CurrentUser.OpenSubKey(CompatKey))
            {
                var raw = key?.GetValue(exePath) as string;
                backup.CompatExisted = raw != null;
                backup.CompatRaw = raw ?? "";
            }

            using (var key = Registry.CurrentUser.OpenSubKey(MouseKey))
            {
                var speed = key?.GetValue("MouseSpeed") as string;
                backup.MouseExisted = speed != null;
                backup.MouseSpeed = speed ?? "";
                backup.MouseThreshold1 = key?.GetValue("MouseThreshold1") as string ?? "";
                backup.MouseThreshold2 = key?.GetValue("MouseThreshold2") as string ?? "";
            }

            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup,
                new JsonSerializerOptions { WriteIndented = true }));

            return (true, "已备份当前设置：" + backup.Time.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex)
        {
            return (false, "备份失败：" + ex.Message);
        }
    }

    /// <summary>一键优化：GPU 高性能 + 禁用全屏优化 + 关闭鼠标加速。</summary>
    public static async Task<List<OptimizeStepResult>> RunAsync(
        string exePath, IProgress<string> log, CancellationToken token = default)
    {
        var results = new List<OptimizeStepResult>();

        // 先备份，备份失败就不动手
        var (backupOk, backupMsg) = CreateBackup(exePath);
        log?.Report(backupOk ? "① " + backupMsg : "① 备份失败，已中止：" + backupMsg);

        if (!backupOk)
        {
            results.Add(new OptimizeStepResult { Name = "备份", Ok = false, Message = backupMsg });
            return results;
        }

        results.Add(new OptimizeStepResult { Name = "备份当前设置", Ok = true, Message = "已保存，可随时回滚" });

        await Task.Delay(120, token);

        // ② GPU 高性能
        var (gpuOk, gpuMsg) = GameTweakService.SetGpuPreference(exePath, true);
        results.Add(new OptimizeStepResult { Name = "高性能 GPU", Ok = gpuOk, Message = gpuMsg });
        log?.Report("② 高性能 GPU：" + gpuMsg);

        await Task.Delay(120, token);

        // ③ 禁用全屏优化
        var (fsOk, fsMsg) = GameTweakService.SetFullscreenOptimization(exePath, true);
        results.Add(new OptimizeStepResult { Name = "禁用全屏优化", Ok = fsOk, Message = fsMsg });
        log?.Report("③ 禁用全屏优化：" + fsMsg);

        await Task.Delay(120, token);

        // ④ 关闭鼠标加速
        var (mouseOk, mouseMsg) = MouseTweakService.DisableAcceleration();
        results.Add(new OptimizeStepResult { Name = "鼠标精准输入", Ok = mouseOk, Message = mouseMsg });
        log?.Report("④ 鼠标精准输入：" + mouseMsg);

        return results;
    }

    /// <summary>一键回滚：把备份里的原值写回去。</summary>
    public static (bool Ok, string Message) Rollback()
    {
        var backup = LoadBackup();
        if (backup == null)
            return (false, "没有找到备份，无法回滚。先点「手动备份」再优化，或者用各项自己的开关单独还原。");

        var notes = new List<string>();

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(GpuPrefKey, writable: true))
            {
                if (key != null)
                {
                    if (backup.GpuPrefExisted)
                        key.SetValue(backup.ExePath, backup.GpuPrefRaw, RegistryValueKind.String);
                    else
                        key.DeleteValue(backup.ExePath, throwOnMissingValue: false);
                    notes.Add("GPU 设置");
                }
            }

            using (var key = Registry.CurrentUser.CreateSubKey(CompatKey, writable: true))
            {
                if (key != null)
                {
                    if (backup.CompatExisted)
                        key.SetValue(backup.ExePath, backup.CompatRaw, RegistryValueKind.String);
                    else
                        key.DeleteValue(backup.ExePath, throwOnMissingValue: false);
                    notes.Add("兼容性设置");
                }
            }

            using (var key = Registry.CurrentUser.CreateSubKey(MouseKey, writable: true))
            {
                if (key != null && backup.MouseExisted)
                {
                    key.SetValue("MouseSpeed", backup.MouseSpeed, RegistryValueKind.String);
                    key.SetValue("MouseThreshold1", backup.MouseThreshold1, RegistryValueKind.String);
                    key.SetValue("MouseThreshold2", backup.MouseThreshold2, RegistryValueKind.String);
                    notes.Add("鼠标设置");
                }
            }

            return (true, "已回滚：" + string.Join("、", notes) + "。重新启动游戏生效。");
        }
        catch (Exception ex)
        {
            return (false, "回滚失败：" + ex.Message);
        }
    }
}
