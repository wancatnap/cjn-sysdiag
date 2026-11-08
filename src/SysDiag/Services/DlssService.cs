using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// DLSS 模型与预设。
///
/// 边界说明（重要）：DLSS 预设写在 NVIDIA 的 DRS 驱动配置里，本机 NVAPI 能建立会话、
/// 读 base profile，但按名字定位游戏 profile 返回 PROFILE_NOT_FOUND，无法保证写对目标配置。
/// 因此这里不直接写驱动，而是生成 NVIDIA Profile Inspector 的标准 .nip 配置文件，
/// 导入后写入的是同一份 DRS 数据，效果一致，且随时能在 NVIDIA 控制面板里还原。
/// </summary>
public static class DlssService
{
    // NVIDIA DRS 字段 ID（与驱动一致的公开值）
    public const uint IdDlssQuality = 0x10AFB768;   // 279951208
    public const uint IdDlssPreset = 0x10E41DF3;    // 283385331
    public const uint IdDlssDllOverride = 0x10E41E01; // 283385345

    public static IReadOnlyList<DlssOption> QualityOptions { get; } = new[]
    {
        new DlssOption(0, "性能优先（50%）", "帧数最高，画面最糊"),
        new DlssOption(1, "均衡模式（58%）", "帧数与画面折中"),
        new DlssOption(2, "画质优先（66%）", "画面最好，帧数最低"),
        new DlssOption(4, "深度学习抗锯齿 DLA（100%）", "不降分辨率，需要驱动 572.83+"),
        new DlssOption(5, "极致性能（33%）", "帧数最高，画面损失明显")
    };

    public static IReadOnlyList<DlssOption> PresetOptions { get; } = new[]
    {
        new DlssOption(0,             "不适用（跟随游戏）"),
        new DlssOption(1,             "预设 A"),
        new DlssOption(2,             "预设 B"),
        new DlssOption(3,             "预设 C"),
        new DlssOption(4,             "预设 D"),
        new DlssOption(5,             "预设 E"),
        new DlssOption(6,             "预设 F"),
        new DlssOption(10,            "预设 J（Transformer）"),
        new DlssOption(11,            "预设 K（最新 Transformer）", "抗锯齿优先，可能会降低帧率"),
        new DlssOption(0x00FFFFFF,    "始终使用最新", "跟随 DLL 内置的默认预设")
    };

    public static IReadOnlyList<DlssOption> OverrideOptions { get; } = new[]
    {
        new DlssOption(0, "关闭（用游戏自带的 DLSS）"),
        new DlssOption(1, "开启（用最新安装的 DLSS）")
    };

    public static string ChoiceFile => Path.Combine(AppPaths.DataDir, "dlss-choice.json");
    public static string NipDir => Path.Combine(AppPaths.OutputDir, "dlss");

    // ------------------------------------------------------------------ 检测

    public static DlssEnvironment Detect(string gameDir = "")
    {
        var gpu = "";
        var driver = "";

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = (mo["Name"] as string ?? "").Trim();
                if (name.Length == 0) continue;
                if (!name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                    continue;

                gpu = name;
                driver = NormalizeDriver(mo["DriverVersion"] as string ?? "");
                break;
            }
        }
        catch { }

        var dir = string.IsNullOrEmpty(gameDir) ? FindGameDirCached() : gameDir;
        var (dlssPath, dlssVer) = ReadDlssVersion(dir, "nvngx_dlss.dll");
        var (_, dlssGVer) = ReadDlssVersion(dir, "nvngx_dlssg.dll");

        return new DlssEnvironment
        {
            GpuName = string.IsNullOrEmpty(gpu) ? "未检测到 NVIDIA 显卡" : gpu,
            DriverVersion = driver,
            GameDir = dir,
            GameExe = FindGameExe(dir),
            DlssPath = dlssPath,
            DlssVersion = dlssVer,
            DlssGVersion = dlssGVer
        };
    }

    /// <summary>32.0.15.8180 → 581.80</summary>
    public static string NormalizeDriver(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return raw;

        var tail = digits[^5..];
        return $"{tail[..3]}.{tail[3..]}";
    }

    private static (string Path, string Version) ReadDlssVersion(string gameRoot, string fileName)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot))
            return ("", "");

        try
        {
            var hit = Directory.EnumerateFiles(gameRoot, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (hit == null) return ("", "");

            var ver = FileVersionInfo.GetVersionInfo(hit);
            var text = ver.FileVersion ?? "";
            if (string.IsNullOrWhiteSpace(text))
                text = ver.ProductVersion ?? "";

            return (hit, NormalizeDlssVersion(text));
        }
        catch
        {
            return ("", "");
        }
    }

    private static string FindGameExe(string gameRoot)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return "";

        try
        {
            return Directory.EnumerateFiles(gameRoot, "DeltaForceClient*.exe", SearchOption.AllDirectories)
                       .FirstOrDefault()
                   ?? "";
        }
        catch { return ""; }
    }

    private static string _cachedGameDir = "";

    private static string FindGameDirCached()
    {
        if (!string.IsNullOrEmpty(_cachedGameDir) && Directory.Exists(_cachedGameDir))
            return _cachedGameDir;

        _cachedGameDir = FindGameDir();
        return _cachedGameDir;
    }

    /// <summary>按固定盘广度搜索 DeltaForceClient-Win64-Shipping.exe，最多 8 层、限时 8 秒。</summary>
    public static string FindGameDir()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();

        var skip = new[] { "windows", "$recycle.bin", "programdata", "appdata", "node_modules",
                           "system volume information", "perflogs", "python", "anaconda" };

        var deadline = DateTime.UtcNow.AddSeconds(8);

        foreach (var root in drives)
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;

            while (queue.Count > 0)
            {
                if (DateTime.UtcNow > deadline) break;
                if (++visited > 60000) break;

                var (path, depth) = queue.Dequeue();
                if (depth > 8) continue;

                try
                {
                    var exe = Directory.EnumerateFiles(path, "DeltaForceClient-Win64-Shipping.exe",
                        SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (exe != null)
                    {
                        // 返回上溯到游戏根（含 DeltaForce 目录那层）
                        var dir = Path.GetDirectoryName(exe)!;
                        for (var i = 0; i < 3; i++)
                        {
                            var parent = Path.GetDirectoryName(dir);
                            if (parent == null) break;
                            dir = parent;
                        }
                        return dir;
                    }

                    foreach (var sub in Directory.EnumerateDirectories(path))
                    {
                        var name = Path.GetFileName(sub);
                        if (skip.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
                catch
                {
                    // 无权限的目录直接跳过
                }
            }
        }

        return "";
    }

    // ------------------------------------------------------------------ 方案

    public static DlssChoice ApplyPreset(string mode)
    {
        var choice = new DlssChoice { PresetMode = mode, PresetLetter = 11, DllOverride = 1 };

        choice.QualityLevel = mode switch
        {
            "帧率优先" => 0,
            "画质优先" => 2,
            _ => 1
        };

        return choice;
    }

    public static DlssChoice LoadChoice()
    {
        try
        {
            if (File.Exists(ChoiceFile))
                return JsonSerializer.Deserialize<DlssChoice>(File.ReadAllText(ChoiceFile)) ?? new DlssChoice();
        }
        catch { }
        return new DlssChoice { PresetMode = "均衡" };
    }

    public static (bool Ok, string Message, string NipPath) Save(DlssEnvironment env, DlssChoice choice)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(ChoiceFile, JsonSerializer.Serialize(choice,
                new JsonSerializerOptions { WriteIndented = true }));

            if (!env.GameFound)
                return (false, "没找到游戏目录，配置只保存到本地，没有生成导入文件。", "");

            Directory.CreateDirectory(NipDir);

            var profileName = Path.GetFileName(env.GameDir.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(profileName)) profileName = "Delta Force";

            var exeName = string.IsNullOrEmpty(env.GameExe)
                ? "deltaforceclient-win64-shipping.exe"
                : Path.GetFileName(env.GameExe).ToLowerInvariant();

            var xml = BuildNip(profileName, exeName, choice);
            var path = Path.Combine(NipDir, $"DLSS-{profileName}-{DateTime.Now:MMdd-HHmm}.nip");
            File.WriteAllText(path, xml, new UTF8Encoding(false));

            return (true, "已保存配置并生成导入文件。", path);
        }
        catch (Exception ex)
        {
            return (false, "保存失败：" + ex.Message, "");
        }
    }

    public static void ClearChoice()
    {
        try { if (File.Exists(ChoiceFile)) File.Delete(ChoiceFile); } catch { }
    }

    /// <summary>生成 NVIDIA Profile Inspector 可导入的 .nip（同一份 DRS 数据）。</summary>
    public static string BuildNip(string profileName, string exeName, DlssChoice choice)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<ArrayOfProfile xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">");
        sb.AppendLine("  <Profile>");
        sb.AppendLine($"    <ProfileName>{Escape(profileName)}</ProfileName>");
        sb.AppendLine("    <Executeables>");
        sb.AppendLine($"      <string>{Escape(exeName)}</string>");
        sb.AppendLine("    </Executeables>");
        sb.AppendLine("    <Settings>");

        AppendSetting(sb, "DLSS - Quality Level", IdDlssQuality, choice.QualityLevel);
        AppendSetting(sb, "DLSS - Forced Preset Letter", IdDlssPreset, choice.PresetLetter);
        AppendSetting(sb, "DLSS - DLL Override", IdDlssDllOverride, choice.DllOverride);

        sb.AppendLine("    </Settings>");
        sb.AppendLine("  </Profile>");
        sb.AppendLine("</ArrayOfProfile>");
        return sb.ToString();
    }

    private static void AppendSetting(StringBuilder sb, string name, uint id, int value)
    {
        sb.AppendLine("      <ProfileSetting>");
        sb.AppendLine($"        <SettingNameInfo>{Escape(name)}</SettingNameInfo>");
        sb.AppendLine($"        <SettingID>{id}</SettingID>");
        sb.AppendLine($"        <SettingValue>{value}</SettingValue>");
        sb.AppendLine("        <ValueType>Dword</ValueType>");
        sb.AppendLine("      </ProfileSetting>");
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string PresetName(int value) =>
        PresetOptions.FirstOrDefault(o => o.Value == value)?.Label ?? value.ToString();

    public static string QualityName(int value) =>
        QualityOptions.FirstOrDefault(o => o.Value == value)?.Label ?? value.ToString();

    /// <summary>"3,1,1,0" / "3.1.1.0" → "3.1.1"</summary>
    public static string NormalizeDlssVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var parts = raw.Replace(',', '.').Replace(' ', ' ')
                       .Split('.', StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => p.Length > 0)
                       .Take(3)
                       .ToArray();

        return parts.Length == 0 ? raw.Trim() : string.Join(".", parts);
    }

    /// <summary>DLSS 版本是否支持预设 J / K（Transformer 模型需要 310.x 以上）。</summary>
    public static string PresetSupportHint(string dlssVersion)
    {
        if (string.IsNullOrWhiteSpace(dlssVersion)) return "读不到 DLL 版本，无法判断是否支持预设 K。";

        var head = dlssVersion.Split('.')[0];
        if (!int.TryParse(new string(head.Where(char.IsDigit).ToArray()), out var major))
            return $"当前 {dlssVersion}，无法解析主版本号。";

        if (major >= 310)
            return $"当前 {dlssVersion}，支持预设 J / K。";

        if (major == 3)
            return $"当前 {dlssVersion}，属于 3.x，预设 K 不会生效；想用 K 要先换成 310.x 以上的 nvngx_dlss.dll。";

        return $"当前 {dlssVersion}，版本号不以 310 开头，无法判定是否支持预设 K。";
    }
}
