using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 显示器 ICC 配置：枚举、应用、还原、导入导出。
///
/// 关键结论（都是实测出来的，不是照抄文档）：
///
/// 1. **关联存在 HKCU**：`Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\`
///    `{4d36e96e-e325-11ce-bfc1-08002be10318}\&lt;实例号&gt;`，值是 REG_MULTI_SZ 的 `ICMProfile`
///    加一个 DWORD `UsePerUserProfiles`。写在 HKCU，**不需要管理员**。
/// 2. **写完必须强制重读**：改注册表本身不会刷新画面，要调一次
///    `ChangeDisplaySettingsEx(null, NULL, NULL, 0, NULL)`（屏幕会闪一下，正常）。
///    不调用的话「点了没反应」，还原时也会卡在上一个配置上——实测踩过。
/// 3. **`WcsAssociateColorProfileWithDevice` 返回 True 但没效果**（本机 mscms 这条路不通），
///    `GetICMProfile` 也只反映系统级关联。所以判断「有没有生效」不能靠它，只能靠注册表 + 像素。
/// 4. **绝对路径能用**：不装进系统 color 目录、直接写 `D:\...\xxx.icc` 也会生效，
///    所以非管理员导入的配置放本程序目录也能用。
/// </summary>
public static class IccService
{
    private const string ClassGuid = "{4d36e96e-e325-11ce-bfc1-08002be10318}";

    private const string AssociationRoot =
        @"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\" + ClassGuid;

    private const string MonitorKeyPrefix = @"SYSTEM\CurrentControlSet\Control\Class\" + ClassGuid + @"\";

    /// <summary>系统公共 color 目录（装到这里，别的程序/系统界面也能看到）。</summary>
    public static string SystemColorDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color");

    /// <summary>本程序自己的配置目录（非管理员导入时落到这里，用绝对路径关联）。</summary>
    public static string DataDir => Path.Combine(AppPaths.DataDir, "icc");

    private static string BackupFile => Path.Combine(AppPaths.DataDir, "icc-backup.json");

    // ---------------------------------------------------------------- 显示器

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll")]
    private static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string device, string port, IntPtr devMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetICMProfile(IntPtr hdc, ref uint size, StringBuilder name);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    /// <summary>
    /// 系统自己的颜色管理 API（Win8+）。「设置 → 显示 → 高级显示 → 颜色配置文件」背后就是它。
    /// 直接写注册表这条路在精简系统上不生效（实测 GDI 一直回报 sRGB），走 API 才认。
    /// scope: 0 = 全局，1 = 当前用户。profileType: 0 = ICC。
    /// </summary>
    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    private static extern int WcsSetDefaultColorProfile(
        int scope, string deviceName, int profileType, int subType, uint profileId, string profileName);

    public static List<IccMonitor> Monitors()
    {
        var list = new List<IccMonitor>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & 0x1) != 0)   // ATTACHED_TO_DESKTOP
            {
                var device = dd.DeviceName;
                var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                EnumDisplayDevices(device, 0, ref monitor, 0);

                var instance = LastSegment(monitor.DeviceID);
                var (current, currentPath) = CurrentProfile(device);

                list.Add(new IccMonitor
                {
                    DeviceName = device,
                    Instance = instance,
                    DeviceId = monitor.DeviceID,
                    Name = FriendlyName(instance, monitor.DeviceID),
                    CurrentProfile = current,
                    CurrentPath = currentPath,
                    PerUserActive = ReadAssociation(instance) is { UsePerUser: true }
                });
            }

            dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }

        return list;
    }

    private static string LastSegment(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "";

        var parts = deviceId.Split('\\');
        return parts.Length > 0 ? parts[^1] : "";
    }

    /// <summary>优先读 EDID 里的型号名（注册表 Device Parameters\EDID 是原始 EDID，型号在 0x36 起的 13 字节）。</summary>
    private static string FriendlyName(string instance, string deviceId)
    {
        try
        {
            var parts = deviceId.Split('\\');
            if (parts.Length >= 2)
            {
                var path = MonitorKeyPrefix + instance + @"\Device Parameters";
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("EDID") is byte[] edid && edid.Length >= 0x6A)
                {
                    var name = Encoding.ASCII.GetString(edid, 0x36, 13).Trim('\0', ' ');
                    if (name.Length > 0) return name;
                }

                return parts[1];   // 例如 HKC2441
            }
        }
        catch { /* 读不到就用设备名 */ }

        return deviceId;
    }

    /// <summary>
    /// GDI 报告的当前配置（反映系统实际认的关联）。
    /// 注意 DC 必须用 CreateDC("DISPLAY", 设备名, …)：把设备名当驱动名传进去
    /// 也能拿到一个 DC，但那是通用 DC，读出来的永远是 sRGB——早期版本踩过这个坑。
    /// </summary>
    public static (string FileName, string FullPath) CurrentProfile(string deviceName)
    {
        var hdc = OpenDisplayDc(deviceName);
        if (hdc == IntPtr.Zero) return ("", "");

        try
        {
            uint size = 520;
            var sb = new StringBuilder((int)size);

            if (!GetICMProfile(hdc, ref size, sb)) return ("", "");

            var path = sb.ToString();
            return (Path.GetFileName(path), path);
        }
        catch
        {
            return ("", "");
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    // ---------------------------------------------------------------- 关联读写

    public sealed record Association(bool UsePerUser, string[] Profiles);

    public static Association ReadAssociation(string instance)
    {
        if (instance.Length == 0) return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AssociationRoot + "\\" + instance);
            if (key == null) return null;

            var use = key.GetValue("UsePerUserProfiles") is int n && n != 0;
            var raw = key.GetValue("ICMProfile");

            var profiles = raw switch
            {
                string[] many => many,
                string one when one.Length > 0 => one.Split(','),
                _ => Array.Empty<string>()
            };

            return new Association(use, profiles);
        }
        catch
        {
            return null;
        }
    }

    private sealed class BackupEntry
    {
        public bool UsePerUser { get; set; }
        public string[] Profiles { get; set; } = Array.Empty<string>();
        public string Time { get; set; } = "";
    }

    private static Dictionary<string, BackupEntry> LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupFile)) return new Dictionary<string, BackupEntry>();

            return JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(File.ReadAllText(BackupFile))
                   ?? new Dictionary<string, BackupEntry>();
        }
        catch
        {
            return new Dictionary<string, BackupEntry>();
        }
    }

    private static void SaveBackup(Dictionary<string, BackupEntry> data)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 备份失败不阻塞操作，只是少一层保险 */ }
    }

    public static int BackupCount => LoadBackup().Count;

    // ---------------------------------------------------------------- 应用 / 还原

    /// <summary>
    /// 把某个配置应用到显示器。第一次动某台显示器时会记下原值。
    ///
    /// 做两件事，缺一不可：
    /// 1. 写色彩关联（给 Photoshop / 浏览器这类会读 ICC 的程序用）
    /// 2. 把校准表写进显卡 LUT（这条才让桌面、游戏肉眼可见）
    /// </summary>
    public static (bool Ok, string Message) Apply(
        IccMonitor monitor, IccProfile profile, double strength = 1.0, bool applyLut = true, bool synthIfMissing = false)
    {
        if (monitor == null || profile == null) return (false, "没有选中显示器或配置。");
        if (monitor.Instance.Length == 0) return (false, "读不到显示器实例号，无法写关联。");

        try
        {
            var current = ReadAssociation(monitor.Instance) ?? new Association(false, Array.Empty<string>());

            var backup = LoadBackup();
            if (!backup.ContainsKey(monitor.Instance))
            {
                backup[monitor.Instance] = new BackupEntry
                {
                    UsePerUser = current.UsePerUser,
                    Profiles = current.Profiles,
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                SaveBackup(backup);
            }

            using var key = Registry.CurrentUser.CreateSubKey(AssociationRoot + "\\" + monitor.Instance, writable: true);
            if (key == null) return (false, "无法打开 ICC 关联键。");

            // 值格式跟系统保持一致：文件在 color 目录里就只写文件名（系统按文件名去那个目录找），
            // 写在别处（本程序目录）才写完整路径。
            // 早期版本一律写完整路径，系统解析不到 → 关联看着写进去了其实没生效。
            var profileValue = profile.FullPath;
            var colorDir = SystemColorDir.TrimEnd('\\');

            if (string.Equals(Path.GetDirectoryName(profile.FullPath)?.TrimEnd('\\'), colorDir,
                    StringComparison.OrdinalIgnoreCase))
            {
                profileValue = Path.GetFileName(profile.FullPath);
            }

            key.SetValue("ICMProfile", new[] { profileValue }, RegistryValueKind.MultiString);

            if (current.Profiles.Length > 0)
                key.SetValue("ICMProfileSnapshot", current.Profiles, RegistryValueKind.MultiString);

            // 这个开关必须为 1，否则系统仍然用 HKLM 的默认关联，等于没关联
            key.SetValue("UsePerUserProfiles", 1, RegistryValueKind.DWord);

            // 注册表写完之后再走一遍系统 API：这一步让 WCS 层（Photoshop 等）真正认这个关联。
            // 注意 API 是「追加」语义：只要注册表里已经有同名项，它就返回 S_FALSE 不再追加，
            // 所以两边必须用同一个名字（都是文件名 / 都是完整路径），否则列表会越加越长。
            var hr = TryWcsAssociate(monitor.DeviceName, profileValue);

            ForceApply();

            // 验证：GDI 的 GetICMProfile 只反映「系统级」关联，
            // 看不到 per-user 设置（这是 Windows 的老接口特性），所以不能用它判定成功与否。
            // 这里只当附加信息用。
            var linked = WaitForGdiReport(monitor.DeviceName, profile.FileName);

            var lines = new List<string>();

            if (linked)
            {
                lines.Add("色彩关联：已生效（系统级和当前用户级都认了这个文件）");
            }
            else if (hr >= 0)
            {
                lines.Add("色彩关联：已按当前用户写入（Photoshop、浏览器这类走 Windows 颜色系统的程序会用它）");
                lines.Add("  注：GDI 的 GetICMProfile 只反映系统级关联，看不到用户级设置，所以它仍显示旧文件——正常，不是没生效。");
            }
            else
            {
                lines.Add($"色彩关联：系统 API 调不通（0x{hr:X8}），这台系统可能精简掉了颜色管理组件。");
                lines.Add("  这只影响会读 ICC 的程序，不影响下面的显卡 LUT。");
            }

            if (applyLut)
            {
                var (lutOk, lutMessage, usedVcgt) = ApplyLut(monitor, profile, strength, synthIfMissing);
                lines.Add("");
                lines.Add(lutOk
                    ? "显卡 LUT：" + (usedVcgt ? "已写入（用的是配置自带的校准表）" : "已写入（按色调曲线合成）")
                    : "显卡 LUT：没有生效");
                lines.Add(lutMessage);
            }
            else
            {
                lines.Add("");
                lines.Add("显卡 LUT：按你的设置跳过了。只写关联的话，桌面和游戏不会变，只有 Photoshop 这类读 ICC 的软件才认。");
            }

            return (true, string.Join("\n", lines));
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "写 ICC 关联被拒绝。请用「以管理员身份重启」后再试。");
        }
        catch (Exception ex)
        {
            return (false, "应用失败：" + ex.Message);
        }
    }

    /// <summary>调系统 API 设置当前用户的默认配置。返回 HRESULT（0 = 改动了，1 = S_FALSE 已经是它，负数 = 调不通）。</summary>
    private static int TryWcsAssociate(string deviceName, string profileName)
    {
        try
        {
            return WcsSetDefaultColorProfile(1, deviceName, 0, 0, 0, profileName);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>改完注册表后 GDI 报告会滞后一点点，重试几次看系统到底认没认。</summary>
    private static bool WaitForGdiReport(string deviceName, string fileName)
    {
        for (var i = 0; i < 4; i++)
        {
            var (current, _) = CurrentProfile(deviceName);
            if (current.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return true;

            System.Threading.Thread.Sleep(120);
        }

        return false;
    }

    /// <summary>恢复这台显示器「本程序改之前」的状态。</summary>
    public static (bool Ok, string Message) RestoreDefault(IccMonitor monitor)
    {
        if (monitor == null) return (false, "没有选中显示器。");

        try
        {
            var lines = new List<string>();

            var backup = LoadBackup();

            using (var key = Registry.CurrentUser.OpenSubKey(AssociationRoot + "\\" + monitor.Instance, writable: true))
            {
                if (key == null)
                {
                    lines.Add("色彩关联：本来就没有本程序的记录，不用恢复。");
                }
                else if (backup.TryGetValue(monitor.Instance, out var entry))
                {
                    key.SetValue("UsePerUserProfiles", entry.UsePerUser ? 1 : 0, RegistryValueKind.DWord);
                    key.SetValue("ICMProfile", entry.Profiles, RegistryValueKind.MultiString);

                    ForceApply();

                    backup.Remove(monitor.Instance);
                    SaveBackup(backup);

                    lines.Add($"色彩关联：已恢复成改动前（UsePerUserProfiles = {(entry.UsePerUser ? 1 : 0)}，清单 {entry.Profiles.Length} 项）。");
                }
                else
                {
                    // 没有备份记录：退回系统默认（关掉用户级关联）
                    key.SetValue("UsePerUserProfiles", 0, RegistryValueKind.DWord);

                    ForceApply();

                    lines.Add("色彩关联：没有本程序的备份记录，已关闭用户级关联，回到系统默认配置。");
                }
            }

            var (lutOk, lutMessage) = RestoreLut(monitor);
            lines.Add("");
            lines.Add("显卡 LUT：" + lutMessage);
            if (!lutOk) lines.Add("（关联已恢复，但 LUT 没还原成功，可以注销一次让系统重置）");

            return (true, string.Join("\n", lines));
        }
        catch (Exception ex)
        {
            return (false, "恢复失败：" + ex.Message);
        }
    }

    /// <summary>强制让系统重读显示配置。不调这个，改注册表不会刷新画面。</summary>
    private static void ForceApply()
    {
        try { ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); } catch { }

        try
        {
            SendMessageTimeout((IntPtr)0xffff, 0x001A, IntPtr.Zero,
                @"Software\Microsoft\Windows NT\CurrentVersion\ICM", 2, 1500, out _);
        }
        catch { }
    }

    // ---------------------------------------------------------------- 显卡 LUT
    //
    // 这是「看得见」的唯一通道，也是本功能最关键的一环。
    //
    // 实测结论（本机 HKC2441）：套了 ICC 之后系统 LUT 一直是线性的
    // （0 / 16448 / 32896 / 49344 / 65535，即 i×257），说明 ICC 里的校准表
    // 从来没被写进显卡——Windows 只在登录、切换配置等时机才去加载，
    // 而且只认系统级关联；per-user 关联 + 绝对路径这条路它不管。
    // 所以必须由我们主动 SetDeviceGammaRamp。
    //
    // SetDeviceGammaRamp 返回 true 也可能被驱动静默忽略，所以每次写完都要回读校验。

    private static string LutBackupFile => Path.Combine(AppPaths.DataDir, "icc-lut-backup.json");

    private static string LutStateFile => Path.Combine(AppPaths.DataDir, "icc-lut-state.json");

    /// <summary>某台显示器上次套用的配置，用于重启后重放。</summary>
    public sealed class LutState
    {
        public string ProfilePath { get; set; } = "";
        public double Strength { get; set; } = 1;
        public bool AutoReapply { get; set; }
    }

    private static IntPtr OpenDisplayDc(string deviceName)
    {
        var hdc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        return hdc != IntPtr.Zero ? hdc : CreateDC(deviceName, null, null, IntPtr.Zero);
    }

    public static ushort[] ReadRamp(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;

        var hdc = OpenDisplayDc(deviceName);
        if (hdc == IntPtr.Zero) return null;

        try
        {
            var ramp = new ushort[768];
            return GetDeviceGammaRamp(hdc, ramp) ? ramp : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    public static bool WriteRamp(string deviceName, ushort[] ramp)
    {
        if (string.IsNullOrEmpty(deviceName) || ramp is not { Length: 768 }) return false;

        var hdc = OpenDisplayDc(deviceName);
        if (hdc == IntPtr.Zero) return false;

        try
        {
            return SetDeviceGammaRamp(hdc, ramp);
        }
        catch
        {
            return false;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    public static bool IsLinear(ushort[] ramp)
    {
        if (ramp is not { Length: 768 }) return false;

        for (var i = 0; i < 256; i++)
        {
            var v = i * 257;
            if (Math.Abs(ramp[i] - v) > 24) return false;
            if (Math.Abs(ramp[256 + i] - v) > 24) return false;
            if (Math.Abs(ramp[512 + i] - v) > 24) return false;
        }

        return true;
    }

    private static int MaxDiff(ushort[] a, ushort[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return int.MaxValue;

        var max = 0;
        for (var i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));

        return max;
    }

    /// <summary>给人看的一句话状态。</summary>
    public static string LutStatus(IccMonitor monitor)
    {
        if (monitor == null) return "没有选中显示器";

        var ramp = ReadRamp(monitor.DeviceName);
        if (ramp == null) return "读不到（HDR 开启或驱动不支持）";

        var hasBackup = LoadJson<Dictionary<string, string>>(LutBackupFile)?.ContainsKey(monitor.DeviceName) == true;

        if (IsLinear(ramp)) return hasBackup ? "线性（当前没有生效，备份还在）" : "线性（没有任何校准生效）";

        return hasBackup ? "本程序的配置生效中" : "非线性（被别的程序或驱动改过）";
    }

    /// <summary>
    /// 把配置写进显卡 LUT。这是「画面真的变了」的那一步。
    /// </summary>
    /// <param name="strength">0~1，和原始 LUT 混合，用来控制强度。</param>
    /// <param name="synthIfMissing">配置里没有 vcgt 时，是否按它的色调曲线合成一条。</param>
    public static (bool Ok, string Message, bool UsedVcgt) ApplyLut(
        IccMonitor monitor, IccProfile profile, double strength, bool synthIfMissing)
    {
        if (monitor == null || profile == null) return (false, "没有选中显示器或配置。", false);

        if (!File.Exists(profile.FullPath)) return (false, "配置文件不见了：" + profile.FullPath, false);

        strength = Math.Clamp(strength, 0, 1);

        var vcgt = IccReader.ReadVcgt(profile.FullPath);
        var usedVcgt = vcgt.IsValid;
        string origin;

        if (usedVcgt)
        {
            origin = "配置自带的校准表 vcgt";
        }
        else if (synthIfMissing)
        {
            vcgt = IccReader.Synthesize(profile.FullPath);
            origin = "按配置色调曲线合成的表（近似）";

            if (!vcgt.IsValid)
                return (false,
                    "这个配置既没有校准表（vcgt），也读不到可用的色调曲线，生成不了可见效果。\n\n" +
                    "它只是一份色彩空间声明（例如 sRGB / 广色域描述），本身不含任何「怎么改屏幕」的数据。", false);
        }
        else
        {
            return (false,
                "这个配置**不含校准表（vcgt）**，只有色彩空间描述。\n\n" +
                "桌面、游戏、视频播放器都不读这种描述，所以套上去画面不会有任何变化——这是 ICC 机制本身的限制，不是没生效。\n\n" +
                "勾选「没有校准表时按色调曲线合成」，可以强行取它的 gamma 曲线做近似效果。", false);
        }

        var current = ReadRamp(monitor.DeviceName);
        if (current == null)
            return (false,
                "读不到当前显卡 LUT。常见原因：显示器开着 HDR（HDR 下系统接管 LUT）、显卡驱动不支持、或当前是远程桌面会话。", usedVcgt);

        // 首次改动前存原始 LUT，之后一直以它为基线（强度混合也基于它）
        var backup = LoadJson<Dictionary<string, string>>(LutBackupFile) ?? new Dictionary<string, string>();

        if (!backup.ContainsKey(monitor.DeviceName))
        {
            backup[monitor.DeviceName] = RampToBase64(current);
            SaveJson(LutBackupFile, backup);
        }
        else
        {
            // 上一条记录指向的显示器可能换了，但设备名不变 → 仍用第一次的原始值
        }

        var baseline = RampFromBase64(backup[monitor.DeviceName]) ?? current;
        var target = vcgt.ToRamp(baseline, strength);

        if (!WriteRamp(monitor.DeviceName, target))
            return (false,
                "显卡驱动拒绝了这次 LUT 写入。常见原因：\n" +
                "· 显示器开着 HDR——关掉 HDR 再试\n" +
                "· N 卡「数字振动」/ A 卡「自定义颜色」正在接管输出——先恢复默认\n" +
                "· 远程桌面会话不支持改 LUT", usedVcgt);

        // 回读校验：有些驱动返回 true 但实际不写
        var verify = ReadRamp(monitor.DeviceName);
        var diff = MaxDiff(verify, target);

        if (diff > 3000)
            return (false,
                "写进去了，但驱动没有接受（回读和预期差 " + diff + "/65535）。\n\n" +
                "这台机器的色彩输出被别的东西接管了：HDR、显卡驱动的颜色设置、或笔记本的厂商色彩软件。\n" +
                "先关掉它们再试。", usedVcgt);

        SaveLutState(monitor.DeviceName, profile.FullPath, strength, LoadLutState().TryGetValue(monitor.DeviceName, out var old) && old.AutoReapply);

        var delta = MaxDiff(target, baseline);
        var percent = delta * 100.0 / 65535;

        return (true,
            $"已写入显卡 LUT：{origin}\n" +
            $"强度 {strength * 100:F0}%　·　相对原始的最大变化 {percent:F1}%\n\n" +
            (percent < 1.5
                ? "变化量很小，肉眼可能看不出来——这个配置的曲线本来就贴近线性。\n"
                : "现在整个桌面、全屏游戏、视频播放器的输出都变了（HDR 模式除外）。\n") +
            "想退回去点「还原 LUT」。", usedVcgt);
    }

    /// <summary>把显卡 LUT 还原成改动前的样子。</summary>
    public static (bool Ok, string Message) RestoreLut(IccMonitor monitor)
    {
        if (monitor == null) return (false, "没有选中显示器。");

        var backup = LoadJson<Dictionary<string, string>>(LutBackupFile) ?? new Dictionary<string, string>();
        var had = backup.TryGetValue(monitor.DeviceName, out var saved);

        var target = had ? RampFromBase64(saved) : IccReader.VcgtData.Linear();
        if (target == null) target = IccReader.VcgtData.Linear();

        if (!WriteRamp(monitor.DeviceName, target))
            return (false, "写回失败，驱动拒绝了。可以先注销再登录，Windows 会自己重置 LUT。");

        if (had)
        {
            backup.Remove(monitor.DeviceName);
            SaveJson(LutBackupFile, backup);
        }

        var state = LoadLutState();
        if (state.Remove(monitor.DeviceName)) SaveJson(LutStateFile, state);

        return (true, had
            ? "已还原成套用之前的显卡 LUT。"
            : "本来就没有本程序的记录，已把 LUT 重置为线性（等同未校准状态）。");
    }

    // ---------------------------------------------------------------- 手动调色

    private static string TunePresetFile => Path.Combine(AppPaths.DataDir, "color-tune-presets.json");

    public static List<ManualTunePreset> LoadTunePresets() =>
        LoadJson<List<ManualTunePreset>>(TunePresetFile) ?? new List<ManualTunePreset>();

    public static (bool Ok, string Message) SaveTunePreset(string name, ManualTune tune)
    {
        if (string.IsNullOrWhiteSpace(name)) return (false, "先给方案起个名字。");
        if (tune == null) return (false, "没有可保存的参数。");

        try
        {
            var list = LoadTunePresets();
            var clean = name.Trim();

            // 同名覆盖，避免越存越多同名项
            list.RemoveAll(p => string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, ManualTunePreset.From(clean, tune.Clamped()));

            SaveJson(TunePresetFile, list);

            return (true, $"已保存方案「{clean}」。");
        }
        catch (Exception ex)
        {
            return (false, "保存失败：" + ex.Message);
        }
    }

    public static (bool Ok, string Message) DeleteTunePreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (false, "没有选中方案。");

        var list = LoadTunePresets();
        var removed = list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0) return (false, "没有找到这个方案。");

        SaveJson(TunePresetFile, list);

        return (true, $"已删除「{name}」。");
    }

    /// <summary>
    /// 把调色参数直接写进显卡 LUT。实时预览和「应用」走的是同一条路，所见即所得。
    /// 同样遵循「首次改动前存原始 LUT」的约定，所以随时能还原。
    /// </summary>
    public static (bool Ok, string Message) ApplyTune(IccMonitor monitor, ManualTune tune)
    {
        if (monitor == null) return (false, "没有选中显示器。");
        if (tune == null) return (false, "没有调色参数。");

        var current = ReadRamp(monitor.DeviceName);
        if (current == null)
            return (false, "读不到当前显卡 LUT。常见原因：显示器开着 HDR（系统接管 LUT）、驱动不支持、或远程桌面会话。");

        var backup = LoadJson<Dictionary<string, string>>(LutBackupFile) ?? new Dictionary<string, string>();

        if (!backup.ContainsKey(monitor.DeviceName))
        {
            backup[monitor.DeviceName] = RampToBase64(current);
            SaveJson(LutBackupFile, backup);
        }

        var target = tune.BuildRamp();

        if (!WriteRamp(monitor.DeviceName, target))
            return (false, "显卡驱动拒绝了这次 LUT 写入。\n先关掉 HDR、把显卡驱动的颜色设置恢复默认，再试。");

        // 回读校验：写成功不等于生效
        var verify = ReadRamp(monitor.DeviceName);

        if (MaxDiff(verify, target) > 3000)
            return (false, "写进去了但驱动没有接受（回读与预期差得太多）。\nHDR 或驱动的色彩接管会让 LUT 被忽略。");

        if (tune.IsNeutral)
            return (true, "参数是中性值，屏幕已回到未调整状态。");

        return (true, $"已应用：{tune.Summary()}\n相对线性最大变化 {tune.MaxDeltaPercent():F1}%。");
    }

    /// <summary>把调色参数导出成带 vcgt 的 ICC 文件。</summary>
    public static (bool Ok, string Message) ExportTuneIcc(ManualTune tune, string description, string targetPath)
    {
        if (tune == null) return (false, "没有调色参数。");
        if (string.IsNullOrWhiteSpace(targetPath)) return (false, "没有指定保存位置。");

        try
        {
            var bytes = IccWriter.Build(description, tune);

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllBytes(targetPath, bytes);

            return (true,
                $"已导出 {bytes.Length / 1024.0:F1} KB 的 ICC 文件：\n{targetPath}\n\n" +
                "用法一：在本程序「显示器配色」里点「导入 ICC」，再应用。\n" +
                "用法二：双击文件 → Windows 颜色管理里装到这台显示器。\n\n" +
                "注意：gamma 写在色调曲线里，亮度/对比度/RGB 增益写在 vcgt 里；" +
                "不加载 vcgt 的软件只看得到 gamma。");
        }
        catch (Exception ex)
        {
            return (false, "导出失败：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- LUT 持久化

    private static Dictionary<string, LutState> LoadLutState() =>
        LoadJson<Dictionary<string, LutState>>(LutStateFile) ?? new Dictionary<string, LutState>();

    private static void SaveLutState(string deviceName, string profilePath, double strength, bool autoReapply)
    {
        var state = LoadLutState();

        state[deviceName] = new LutState
        {
            ProfilePath = profilePath,
            Strength = strength,
            AutoReapply = autoReapply
        };

        SaveJson(LutStateFile, state);
    }

    public static bool AutoReapply(string deviceName) =>
        deviceName != null
        && LoadLutState().TryGetValue(deviceName, out var s)
        && s.AutoReapply;

    /// <summary>上次用的强度（0~1），没记录时返回 1。</summary>
    public static double SavedStrength(string deviceName)
    {
        if (deviceName != null && LoadLutState().TryGetValue(deviceName, out var s) && s.Strength > 0)
            return s.Strength;

        return 1;
    }

    /// <summary>开关「下次启动自动重新应用」。还没套过配置时返回 false（没有可重放的东西）。</summary>
    public static bool SetAutoReapply(IccMonitor monitor, bool value)
    {
        if (monitor == null) return false;

        var state = LoadLutState();
        if (!state.TryGetValue(monitor.DeviceName, out var entry)) return false;

        entry.AutoReapply = value;
        state[monitor.DeviceName] = entry;
        SaveJson(LutStateFile, state);

        return true;
    }

    /// <summary>开机（本程序启动）时把上次套用的 LUT 重新写一遍——重启后系统不会替我们保持。</summary>
    public static (int Applied, int Failed) ReplaySavedLuts()
    {
        var applied = 0;
        var failed = 0;

        try
        {
            var state = LoadLutState();
            if (state.Count == 0) return (0, 0);

            var monitors = Monitors();

            foreach (var (deviceName, entry) in state)
            {
                if (!entry.AutoReapply) continue;

                var monitor = monitors.FirstOrDefault(m => m.DeviceName == deviceName);
                if (monitor == null) continue;

                if (!File.Exists(entry.ProfilePath)) { failed++; continue; }

                var profile = FromPath(entry.ProfilePath);
                var (ok, _, _) = ApplyLut(monitor, profile, entry.Strength, true);

                if (ok) applied++;
                else failed++;
            }
        }
        catch
        {
            // 开机重放失败不该影响主程序启动
        }

        return (applied, failed);
    }

    public static IccProfile FromPath(string path)
    {
        var info = IccReader.Read(path);

        return new IccProfile
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            Description = info.Description.Length > 0 ? info.Description : Path.GetFileNameWithoutExtension(path),
            ColorSpace = info.ColorSpace,
            Version = info.Version,
            IsMatrix = info.IsMatrix,
            Gamma = info.Gamma,
            IsSystem = path.StartsWith(SystemColorDir, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string RampToBase64(ushort[] ramp)
    {
        var bytes = new byte[ramp.Length * 2];
        Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static ushort[] RampFromBase64(string text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length != 1536) return null;

            var ramp = new ushort[768];
            Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);

            return ramp;
        }
        catch
        {
            return null;
        }
    }

    private static T LoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveJson<T>(string path, T data)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不下不阻塞操作 */ }
    }

    // ---------------------------------------------------------------- 探针

    /// <summary>
    /// 诊断用：把「这条链路上每一环到底通不通」打出来。
    /// 排查「应用了 ICC 但画面没变」时先跑这个——ICC 关联和显卡 LUT 是两条独立的路，
    /// 关联成功不等于看得见。
    /// </summary>
    public static string Probe(bool live = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 显示器 ===");

        var monitors = Monitors();

        foreach (var m in monitors)
        {
            sb.AppendLine($"DEV={m.DeviceName}  INST={m.Instance}  NAME={m.Name}");
            sb.AppendLine($"  DeviceId={m.DeviceId}");
            sb.AppendLine($"  GDI.ICM={m.CurrentProfile}  Path={m.CurrentPath}");

            var assoc = ReadAssociation(m.Instance);
            sb.AppendLine(assoc == null
                ? "  HKCU 关联=<无键>"
                : $"  HKCU UsePerUser={assoc.UsePerUser}  Profiles=[{string.Join(" | ", assoc.Profiles)}]");

            var hdc = CreateDC("DISPLAY", m.DeviceName, null, IntPtr.Zero);
            sb.AppendLine("  CreateDC(DISPLAY)=" + (hdc != IntPtr.Zero ? "ok" : "fail"));

            if (hdc == IntPtr.Zero) hdc = CreateDC(m.DeviceName, null, null, IntPtr.Zero);

            if (hdc != IntPtr.Zero)
            {
                var ramp = new ushort[768];
                var got = GetDeviceGammaRamp(hdc, ramp);

                sb.AppendLine("  GetDeviceGammaRamp=" + got);

                if (got)
                    sb.AppendLine($"    R: {ramp[0]},{ramp[64]},{ramp[128]},{ramp[192]},{ramp[255]}");
                if (got)
                    sb.AppendLine($"    G: {ramp[256]},{ramp[320]},{ramp[384]},{ramp[448]},{ramp[511]}");

                // 写一次再读回来，确认驱动真的接受（有些驱动/开 HDR 时会静默拒绝）
                if (got)
                {
                    var probeRamp = (ushort[])ramp.Clone();
                    for (var i = 0; i < 256; i++)
                    {
                        var v = (ushort)Math.Min(65535, i * 257);
                        probeRamp[i] = v;
                        probeRamp[256 + i] = v;
                        probeRamp[512 + i] = v;
                    }

                    var set = SetDeviceGammaRamp(hdc, probeRamp);
                    sb.AppendLine("  SetDeviceGammaRamp(线性)=" + set);

                    var back = new ushort[768];
                    if (GetDeviceGammaRamp(hdc, back))
                        sb.AppendLine($"    回读 R128={back[128]}（线性应为 32896）");

                    SetDeviceGammaRamp(hdc, ramp);   // 立刻还原
                    sb.AppendLine("  已还原原始 ramp");
                }

                DeleteDC(hdc);
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== ICC 文件（前 20 个）===");

        foreach (var p in Profiles().Take(20))
        {
            var vcgt = IccReader.ReadVcgt(p.FullPath);
            sb.AppendLine($"  {p.FileName}  valid={p.ColorSpace.Length > 0}  gamma={p.Gamma:F2}  matrix={p.IsMatrix}");
            sb.AppendLine("    vcgt=" + vcgt.Describe());

            if (vcgt.IsValid)
            {
                var ramp = vcgt.ToRamp(IccReader.VcgtData.Linear(), 1.0);
                var pts = new[] { 0, 32, 64, 128, 192, 255 };

                foreach (var name in new[] { "R", "G", "B" })
                {
                    var ch = name == "R" ? 0 : name == "G" ? 1 : 2;
                    var vals = pts.Select(i => ramp[ch * 256 + i]).ToArray();
                    sb.AppendLine($"    曲线 {name}: " + string.Join(" / ", vals));
                }

                // 与线性差多少：最大偏差占满量程的百分比，判断「肉眼能不能看出来」
                var maxDelta = 0;
                for (var ch = 0; ch < 3; ch++)
                {
                    for (var i = 0; i < 256; i++)
                    {
                        maxDelta = Math.Max(maxDelta, Math.Abs(ramp[ch * 256 + i] - i * 257));
                    }
                }

                sb.AppendLine($"    最大偏差 = {maxDelta}/65535 = {maxDelta * 100.0 / 65535:F1}%");
            }
        }

        if (live && monitors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== 实测写入（会真的改屏幕，测完立刻还原）===");

            var target = Profiles().FirstOrDefault(p => p.HasVcgt);
            var monitor = monitors[0];

            if (target == null)
            {
                sb.AppendLine("没有找到含 vcgt 的配置，跳过。");
            }
            else
            {
                var before = ReadRamp(monitor.DeviceName);
                sb.AppendLine($"目标配置：{target.FileName}（偏差 {target.VcgtDelta:F1}%）");

                var (ok, message) = Apply(monitor, target, 1.0, true, false);
                var after = ReadRamp(monitor.DeviceName);

                sb.AppendLine($"Apply={ok}");
                sb.AppendLine(message.Replace("\n", "\n    "));
                sb.AppendLine($"  写入前 R128={before?[128]} → 写入后 R128={after?[128]}");
                sb.AppendLine($"  LUT 最大变化 = {MaxDiff(before, after)}/65535");
                sb.AppendLine($"  GDI 现在报告：{CurrentProfile(monitor.DeviceName).FileName}");

                var (restoreOk, restoreMessage) = RestoreDefault(monitor);
                var back = ReadRamp(monitor.DeviceName);

                sb.AppendLine($"RestoreDefault={restoreOk}");
                sb.AppendLine("  " + restoreMessage.Replace("\n", "\n  "));
                sb.AppendLine($"  还原后 R128={back?[128]}（线性应为 32896）");
                sb.AppendLine($"  已回到线性 = {IsLinear(back)}");
                sb.AppendLine($"  GDI 现在报告：{CurrentProfile(monitor.DeviceName).FileName}");
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- 配置清单

    /// <summary>校准表相对线性的最大偏差（百分比）。用来判断「套上去肉眼能不能看出来」。</summary>
    private static double VcgtDelta(IccReader.VcgtData vcgt)
    {
        if (!vcgt.IsValid) return 0;

        try
        {
            var linear = IccReader.VcgtData.Linear();
            var ramp = vcgt.ToRamp(linear, 1.0);

            var max = 0;
            for (var i = 0; i < 768; i++) max = Math.Max(max, Math.Abs(ramp[i] - linear[i]));

            return max * 100.0 / 65535;
        }
        catch
        {
            return 0;
        }
    }

    public static List<IccProfile> Profiles()
    {
        var list = new List<IccProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Scan(string dir, bool isSystem)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(dir, "*.ic*")
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        return ext.Equals(".icc", StringComparison.OrdinalIgnoreCase)
                               || ext.Equals(".icm", StringComparison.OrdinalIgnoreCase);
                    })
                    .Take(300)
                    .ToList();
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                if (!seen.Add(file)) continue;

                var info = IccReader.Read(file);
                var vcgt = IccReader.ReadVcgt(file);

                list.Add(new IccProfile
                {
                    FullPath = file,
                    FileName = Path.GetFileName(file),
                    Description = info.Description.Length > 0 ? info.Description : Path.GetFileNameWithoutExtension(file),
                    ColorSpace = info.ColorSpace,
                    Version = info.Version,
                    IsSystem = isSystem,
                    IsMatrix = info.IsMatrix,
                    Gamma = info.Gamma,
                    SizeText = new FileInfo(file).Length / 1024 + " KB",
                    HasVcgt = vcgt.IsValid,
                    VcgtText = vcgt.Describe(),
                    VcgtDelta = VcgtDelta(vcgt)
                });
            }
        }

        Scan(SystemColorDir, true);
        Scan(DataDir, false);

        return list
            .OrderByDescending(p => p.IsSystem)
            .ThenBy(p => p.Description, StringComparer.CurrentCulture)
            .ToList();
    }

    // ---------------------------------------------------------------- 导入 / 导出

    /// <summary>
    /// 导入一个 ICC：优先装进系统 color 目录（别的程序也能用）；
    /// 没有管理员权限就放到本程序目录，用绝对路径关联（实测可用）。
    /// </summary>
    public static (bool Ok, string Message, IccProfile Profile) Import(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return (false, "文件不存在：" + sourcePath, null);

        var ext = Path.GetExtension(sourcePath);
        if (!ext.Equals(".icc", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".icm", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "只支持 .icc / .icm 文件。", null);
        }

        var name = Path.GetFileName(sourcePath);

        // 先试着装到系统目录
        try
        {
            Directory.CreateDirectory(SystemColorDir);
            var target = Path.Combine(SystemColorDir, name);

            if (!File.Exists(target) || !SameContent(sourcePath, target))
                File.Copy(sourcePath, target, true);

            var info = IccReader.Read(target);
            if (!info.IsValid) return (false, "这个文件不是有效的 ICC 配置（缺 acsp 签名）。", null);

            return (true, $"已导入到系统 color 目录：\n{target}", new IccProfile
            {
                FullPath = target,
                FileName = name,
                Description = info.Description,
                ColorSpace = info.ColorSpace,
                Version = info.Version,
                IsSystem = true,
                IsMatrix = info.IsMatrix,
                Gamma = info.Gamma
            });
        }
        catch (UnauthorizedAccessException)
        {
            // 没有管理员：落到本程序目录
        }
        catch (Exception ex)
        {
            return (false, "导入失败：" + ex.Message, null);
        }

        try
        {
            Directory.CreateDirectory(DataDir);
            var target = Path.Combine(DataDir, name);
            File.Copy(sourcePath, target, true);

            var info = IccReader.Read(target);
            if (!info.IsValid) return (false, "这个文件不是有效的 ICC 配置（缺 acsp 签名）。", null);

            return (true,
                "没有管理员权限，已导入到本程序目录（用绝对路径关联，同样能生效）：\n" + target +
                "\n\n想让系统「颜色管理」里也看到它，用管理员身份重启后再导入一次。",
                new IccProfile
                {
                    FullPath = target,
                    FileName = name,
                    Description = info.Description,
                    ColorSpace = info.ColorSpace,
                    Version = info.Version,
                    IsSystem = false,
                    IsMatrix = info.IsMatrix,
                    Gamma = info.Gamma
                });
        }
        catch (Exception ex)
        {
            return (false, "导入失败：" + ex.Message, null);
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;

            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>导出：把配置原样复制到用户选的位置，不改任何系统设置。</summary>
    public static (bool Ok, string Message) Export(IccProfile profile, string targetPath)
    {
        if (profile == null || !File.Exists(profile.FullPath)) return (false, "找不到这个配置文件。");

        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(profile.FullPath, targetPath, true);

            return (true, $"已导出到：\n{targetPath}");
        }
        catch (Exception ex)
        {
            return (false, "导出失败：" + ex.Message);
        }
    }

    /// <summary>删除本程序目录里的导入配置（系统目录的不动）。</summary>
    public static (bool Ok, string Message) Delete(IccProfile profile)
    {
        if (profile == null) return (false, "没有选中配置。");
        if (profile.IsSystem) return (false, "系统 color 目录里的配置不在这里删（可能被别的程序使用）。");

        try
        {
            File.Delete(profile.FullPath);
            return (true, "已删除：" + profile.FileName);
        }
        catch (Exception ex)
        {
            return (false, "删除失败：" + ex.Message);
        }
    }
}

/// <summary>极简 ICC 解析：只读描述、色彩空间、版本和 TRC（够界面用，不解析 LUT）。</summary>
internal static class IccReader
{    internal sealed class IccInfo
    {
        public bool IsValid { get; init; }
        public string Description { get; init; } = "";
        public string ColorSpace { get; init; } = "";
        public string Version { get; init; } = "";
        public bool IsMatrix { get; init; }
        public double Gamma { get; init; }
    }

    /// <summary>
    /// vcgt（Video Card Gamma Table）——ICC 里**唯一能真正改变全屏输出**的标签。
    ///
    /// 关键事实：ICC 的 XYZ/TRC 只是「色彩描述」，桌面和游戏根本不读，所以关联 ICC 后观感不变是正常的。
    /// 校准工具（Windows 校准、DisplayCAL）真正落地的东西是这个 vcgt：一条 RGB 查找表，
    /// 由系统通过 SetDeviceGammaRamp 写进显卡 LUT，才「看得见」。
    /// 绝大多数厂商官网下的 .icm 不含 vcgt —— 这就解释了「套了没效果」。
    ///
    /// 布局（Adobe/Apple 私有标签）：
    ///   0  sig='vcgt'   4  reserved   8  type
    ///   type=0（表格）: 12 channels(u16) 14 entryCount(u16) 16 entrySize(u16) 18 data（逐通道分块，大端）
    ///   type=1（公式）: 12 gamma(u1Fixed15) 16 min 20 max
    /// </summary>
    internal sealed class VcgtData
    {
        public int Type { get; init; } = -1;          // -1 无 / 0 表格 / 1 公式
        public int Channels { get; init; } = 3;
        public int EntryCount { get; init; }
        public ushort[] Table { get; init; } = Array.Empty<ushort>();
        public double Gamma { get; init; }
        public double Min { get; init; }
        public double Max { get; init; } = 1.0;

        public bool IsValid => Type >= 0;

        public string Describe() => Type switch
        {
            -1 => "无（只有色彩描述，不改变屏幕输出）",
            0 => $"表格型 {Channels}×{EntryCount} 项（可写显卡 LUT，全屏可见）",
            1 => $"公式型 gamma={Gamma:F2}（可写显卡 LUT，全屏可见）",
            _ => "未知类型 " + Type
        };

        /// <summary>转成 3×256 的显卡 LUT，再按 strength 与 baseline 混合（0 = 原样，1 = 完全应用）。</summary>
        public ushort[] ToRamp(ushort[] baseline, double strength)
        {
            var ramp = baseline is { Length: 768 } ? (ushort[])baseline.Clone() : Linear();

            if (!IsValid) return ramp;

            var t = Math.Clamp(strength, 0, 1);
            if (t <= 0.0001) return ramp;

            for (var ch = 0; ch < 3; ch++)
            {
                for (var i = 0; i < 256; i++)
                {
                    double target;

                    if (Type == 1)
                    {
                        var v = Math.Pow(i / 255.0, Gamma);
                        target = (Min + (Max - Min) * v) * 65535.0;
                    }
                    else
                    {
                        // 通道数不足 3 时（灰度配置）最后一条复制到其余通道
                        var source = Math.Min(ch, Channels - 1);
                        target = Sample(source, i / 255.0) * 65535.0;
                    }

                    var idx = ch * 256 + i;
                    ramp[idx] = (ushort)Math.Clamp(Math.Round(ramp[idx] * (1 - t) + target * t), 0, 65535);
                }
            }

            return ramp;
        }

        private double Sample(int channel, double x)
        {
            if (EntryCount <= 0 || Table.Length < (channel + 1) * EntryCount) return x;

            var f = Math.Clamp(x, 0, 1) * (EntryCount - 1);
            var i0 = (int)Math.Floor(f);
            var i1 = Math.Min(i0 + 1, EntryCount - 1);
            var frac = f - i0;

            var a = Table[channel * EntryCount + i0] / 65535.0;
            var b = Table[channel * EntryCount + i1] / 65535.0;

            return a + (b - a) * frac;
        }

        /// <summary>单位 LUT：i → i*257，即「不做任何改变」。</summary>
        public static ushort[] Linear()
        {
            var ramp = new ushort[768];
            for (var i = 0; i < 256; i++)
            {
                var v = (ushort)(i * 257);
                ramp[i] = v;
                ramp[256 + i] = v;
                ramp[512 + i] = v;
            }

            return ramp;
        }
    }

    /// <summary>解析标签表。sig → (偏移, 长度)。</summary>
    private static Dictionary<string, (int Offset, int Size)> ReadTags(byte[] data)
    {
        var tags = new Dictionary<string, (int, int)>();
        if (data.Length < 132) return tags;

        var tagCount = (int)Be32(data, 128);
        if (tagCount <= 0 || tagCount > 300) return tags;

        for (var i = 0; i < tagCount; i++)
        {
            var at = 132 + i * 12;
            if (at + 12 > data.Length) break;

            var sig = Encoding.ASCII.GetString(data, at, 4);
            var offset = (int)Be32(data, at + 4);
            var size = (int)Be32(data, at + 8);

            if (offset > 0 && size > 0 && offset + size <= data.Length)
                tags[sig] = (offset, size);
        }

        return tags;
    }

    /// <summary>
    /// 从 r/g/bTRC 合成一条等效 LUT。
    /// 用在「配置没有 vcgt」的场合：把它的色调曲线落到显卡上，至少能看到 gamma 层面的差别。
    /// 注意这是近似——不含色域映射（那部分只有色彩管理程序能做）。
    /// </summary>
    public static VcgtData Synthesize(string path)
    {
        try
        {
            if (!File.Exists(path)) return new VcgtData();

            var data = File.ReadAllBytes(path);
            if (data.Length < 132 || Encoding.ASCII.GetString(data, 36, 4) != "acsp") return new VcgtData();

            var tags = ReadTags(data);
            var table = new ushort[768];
            var curves = new double[3][];

            var sigs = new[] { "rTRC", "gTRC", "bTRC" };
            for (var c = 0; c < 3; c++)
            {
                if (!tags.TryGetValue(sigs[c], out var tag)) return new VcgtData();
                curves[c] = ReadCurve(data, tag.Offset);
                if (curves[c] == null) return new VcgtData();
            }

            for (var c = 0; c < 3; c++)
            {
                for (var i = 0; i < 256; i++)
                    table[c * 256 + i] = (ushort)Math.Clamp(Math.Round(curves[c][i] * 65535), 0, 65535);
            }

            return new VcgtData { Type = 0, Channels = 3, EntryCount = 256, Table = table };
        }
        catch
        {
            return new VcgtData();
        }
    }

    /// <summary>读一条 TRC，重采样成 256 个 0..1 的输出值。支持 curv 与 para 两种类型。</summary>
    private static double[] ReadCurve(byte[] data, int offset)
    {
        if (offset + 12 > data.Length) return null;

        var sig = Encoding.ASCII.GetString(data, offset, 4);
        var result = new double[256];

        if (sig == "curv")
        {
            var count = (int)Be32(data, offset + 8);

            if (count == 0)
            {
                for (var i = 0; i < 256; i++) result[i] = i / 255.0;
                return result;
            }

            if (count == 1)
            {
                if (offset + 14 > data.Length) return null;

                var gamma = Be16(data, offset + 12) / 256.0;
                if (gamma is < 0.05 or > 20) return null;

                for (var i = 0; i < 256; i++) result[i] = Math.Pow(i / 255.0, gamma);
                return result;
            }

            if (count > 8192 || offset + 12 + count * 2 > data.Length) return null;

            for (var i = 0; i < 256; i++)
            {
                var f = i / 255.0 * (count - 1);
                var i0 = (int)Math.Floor(f);
                var i1 = Math.Min(i0 + 1, count - 1);
                var frac = f - i0;

                var a = Be16(data, offset + 12 + i0 * 2) / 65535.0;
                var b = Be16(data, offset + 12 + i1 * 2) / 65535.0;

                result[i] = a + (b - a) * frac;
            }

            return result;
        }

        if (sig != "para") return null;

        var type = Be16(data, offset + 8);
        var p = new double[7];

        for (var i = 0; i < 7; i++)
            p[i] = S15Fixed16(data, offset + 12 + i * 4);

        var pg = p[0];
        var pa = p[1];
        var pb = p[2];
        var pc = p[3];
        var pd = p[4];
        var pe = p[5];
        var pf = p[6];

        if (pg is < 0.05 or > 20) return null;

        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;

            var y = type switch
            {
                0 => Math.Pow(x, pg),
                1 => x >= -pb / pa ? Math.Pow(pa * x + pb, pg) : 0,
                2 => x >= -pb / pa ? Math.Pow(pa * x + pb, pg) + pc : pc,
                3 => x >= pd ? Math.Pow(pa * x + pb, pg) : pc * x,
                4 => x >= pd ? Math.Pow(pa * x + pb, pg) + pe : pc * x + pf,
                _ => double.NaN
            };

            if (double.IsNaN(y)) return null;

            result[i] = Math.Clamp(y, 0, 1);
        }

        return result;
    }

    private static double S15Fixed16(byte[] d, int o) =>
        o + 4 <= d.Length ? (int)(uint)Be32(d, o) / 65536.0 : 0;

    public static IccInfo Read(string path)
    {        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            if (fs.Length < 132) return new IccInfo { IsValid = false };

            var data = reader.ReadBytes((int)Math.Min(fs.Length, 256 * 1024));

            if (Encoding.ASCII.GetString(data, 36, 4) != "acsp")
                return new IccInfo { IsValid = false };

            var major = data[8];
            var minor = data[9] >> 4;   // 版本号次位在高 4 位：0x10 → .1、0x40 → .0
            var colorSpace = Encoding.ASCII.GetString(data, 16, 4).Trim();

            var tagCount = BitConverter.ToUInt32(Flip(data, 128, 4), 0);
            if (tagCount == 0 || tagCount > 200) return new IccInfo { IsValid = true, ColorSpace = colorSpace };

            var tags = new Dictionary<string, (int Offset, int Size)>();

            for (var i = 0; i < tagCount; i++)
            {
                var at = 132 + i * 12;
                if (at + 12 > data.Length) break;

                var sig = Encoding.ASCII.GetString(data, at, 4);
                var offset = (int)BitConverter.ToUInt32(Flip(data, at + 4, 4), 0);
                var size = (int)BitConverter.ToUInt32(Flip(data, at + 8, 4), 0);

                if (offset > 0 && size > 0 && offset + size <= data.Length)
                    tags[sig] = (offset, size);
            }

            var desc = tags.TryGetValue("desc", out var d) ? TextDescription(data, d.Offset, d.Size) : "";
            if (desc.Length == 0 && tags.TryGetValue("mluc", out var m)) desc = MultiLocalized(data, m.Offset, m.Size);

            var isMatrix = tags.ContainsKey("rXYZ") && tags.ContainsKey("rTRC");
            var gamma = 0.0;

            if (tags.TryGetValue("rTRC", out var trc))
                gamma = CurveGamma(data, trc.Offset, trc.Size);

            return new IccInfo
            {
                IsValid = true,
                Description = desc.Trim(),
                ColorSpace = colorSpace,
                Version = $"{major}.{minor}",
                IsMatrix = isMatrix,
                Gamma = gamma
            };
        }
        catch
        {
            return new IccInfo { IsValid = false };
        }
    }

    /// <summary>读 vcgt 标签。文件里没有就返回 Type = -1，不报错。</summary>
    public static VcgtData ReadVcgt(string path)
    {
        try
        {
            if (!File.Exists(path)) return new VcgtData();

            var data = File.ReadAllBytes(path);
            if (data.Length < 132) return new VcgtData();
            if (Encoding.ASCII.GetString(data, 36, 4) != "acsp") return new VcgtData();

            var tagCount = (int)Be32(data, 128);
            if (tagCount <= 0 || tagCount > 300) return new VcgtData();

            for (var i = 0; i < tagCount; i++)
            {
                var at = 132 + i * 12;
                if (at + 12 > data.Length) break;

                if (Encoding.ASCII.GetString(data, at, 4) != "vcgt") continue;

                var offset = (int)Be32(data, at + 4);
                if (offset <= 0 || offset + 20 > data.Length) return new VcgtData();

                return ParseVcgt(data, offset);
            }

            return new VcgtData();
        }
        catch
        {
            return new VcgtData();
        }
    }

    private static VcgtData ParseVcgt(byte[] data, int offset)
    {
        var type = (int)Be32(data, offset + 8);

        if (type == 1)
        {
            var gamma = Be16(data, offset + 12) / 32768.0;
            var min = Be16(data, offset + 16) / 32768.0;
            var max = Be16(data, offset + 20) / 32768.0;

            // 合理性校验：读到垃圾值时退回「没有」
            if (gamma is < 0.05 or > 20 || max <= min) return new VcgtData();

            return new VcgtData { Type = 1, Gamma = gamma, Min = min, Max = max };
        }

        if (type != 0) return new VcgtData();

        var channels = Be16(data, offset + 12);
        var count = Be16(data, offset + 14);
        var entrySize = Be16(data, offset + 16);

        if (channels is < 1 or > 4 || count is < 2 or > 8192 || entrySize is < 1 or > 4)
            return new VcgtData();

        var start = offset + 18;
        if (start + channels * count * entrySize > data.Length) return new VcgtData();

        var table = new ushort[channels * count];

        for (var c = 0; c < channels; c++)
        {
            for (var i = 0; i < count; i++)
            {
                var p = start + (c * count + i) * entrySize;

                table[c * count + i] = entrySize switch
                {
                    1 => (ushort)(data[p] * 257),
                    2 => (ushort)Be16(data, p),
                    3 => (ushort)(data[p + 1] * 257),
                    _ => (ushort)Be16(data, p + 2)
                };
            }
        }

        return new VcgtData { Type = 0, Channels = channels, EntryCount = count, Table = table };
    }

    private static int Be16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

    private static long Be32(byte[] d, int o) =>
        ((long)d[o] << 24) | ((long)d[o + 1] << 16) | ((long)d[o + 2] << 8) | d[o + 3];

    private static byte[] Flip(byte[] data, int offset, int count)
    {
        var slice = new byte[count];
        Array.Copy(data, offset, slice, 0, count);
        if (BitConverter.IsLittleEndian) Array.Reverse(slice);
        return slice;
    }

    /// <summary>v2 的 textDescriptionType：sig(4) + reserved(4) + asciiCount(4) + ascii + ...</summary>
    private static string TextDescription(byte[] data, int offset, int size)
    {
        try
        {
            if (size < 12) return "";

            var count = (int)BitConverter.ToUInt32(Flip(data, offset + 8, 4), 0);
            if (count <= 0 || count > 512 || offset + 12 + count > data.Length) return "";

            return Encoding.ASCII.GetString(data, offset + 12, count).TrimEnd('\0');
        }
        catch
        {
            return "";
        }
    }

    /// <summary>v4 的 mluc：记录数(4) + 记录长(4) + 语言/国家(4) + 长度(4) + 偏移(4)，正文是 UTF-16BE。</summary>
    private static string MultiLocalized(byte[] data, int offset, int size)
    {
        try
        {
            if (size < 16) return "";

            var count = (int)BitConverter.ToUInt32(Flip(data, offset + 8, 4), 0);
            if (count <= 0 || count > 20) return "";

            var length = (int)BitConverter.ToUInt32(Flip(data, offset + 20, 4), 0);
            var strOffset = (int)BitConverter.ToUInt32(Flip(data, offset + 24, 4), 0);

            var start = offset + strOffset;
            if (length <= 0 || start + length > data.Length) return "";

            var sb = new StringBuilder(length / 2);
            for (var i = 0; i + 1 < length; i += 2)
                sb.Append((char)((data[start + i] << 8) | data[start + i + 1]));

            return sb.ToString().TrimEnd('\0');
        }
        catch
        {
            return "";
        }
    }

    /// <summary>curv 型 TRC：count == 1 时是 u8Fixed8 的 gamma，否则是查表（返回 0，不做预览）。</summary>
    private static double CurveGamma(byte[] data, int offset, int size)
    {
        try
        {
            if (size < 12) return 0;

            var count = (int)BitConverter.ToUInt32(Flip(data, offset + 8, 4), 0);
            if (count != 1) return 0;

            var fixed88 = (int)BitConverter.ToUInt16(Flip(data, offset + 12, 2), 0);
            return fixed88 / 256.0;
        }
        catch
        {
            return 0;
        }
    }
}
