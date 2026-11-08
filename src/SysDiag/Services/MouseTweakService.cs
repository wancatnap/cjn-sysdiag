using Microsoft.Win32;

namespace SysDiag.Services;

/// <summary>
/// 鼠标精准输入优化：关闭 Windows 的「提高指针精确度」（鼠标加速）。
/// 三个值全 0 就是关掉加速——原始位移和屏幕位移变成 1:1，手感更接近鼠标本身。
/// </summary>
public static class MouseTweakService
{
    private const string MouseKey = @"Control Panel\Mouse";

    /// <summary>Windows 默认值，用于「恢复」。</summary>
    private const string DefaultSpeed = "1";
    private const string DefaultThreshold1 = "6";
    private const string DefaultThreshold2 = "10";

    public static (bool Disabled, string Speed, string T1, string T2) Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MouseKey);
            var speed = key?.GetValue("MouseSpeed") as string ?? DefaultSpeed;
            var t1 = key?.GetValue("MouseThreshold1") as string ?? DefaultThreshold1;
            var t2 = key?.GetValue("MouseThreshold2") as string ?? DefaultThreshold2;

            var disabled = speed == "0" && t1 == "0" && t2 == "0";
            return (disabled, speed, t1, t2);
        }
        catch
        {
            return (false, DefaultSpeed, DefaultThreshold1, DefaultThreshold2);
        }
    }

    /// <summary>关闭鼠标加速。返回是否成功。</summary>
    public static (bool Ok, string Message) DisableAcceleration()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MouseKey, writable: true);
            if (key == null) return (false, "打不开鼠标设置项。");

            key.SetValue("MouseSpeed", "0", RegistryValueKind.String);
            key.SetValue("MouseThreshold1", "0", RegistryValueKind.String);
            key.SetValue("MouseThreshold2", "0", RegistryValueKind.String);

            // 通知系统设置已改变，部分程序无需注销即可生效
            NotifyChange();

            return (true, "已关闭鼠标加速。游戏内重新进入一次即生效；桌面可能需要注销后才有变化。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>恢复到 Windows 默认（开启加速）。</summary>
    public static (bool Ok, string Message) Restore()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MouseKey, writable: true);
            if (key == null) return (false, "打不开鼠标设置项。");

            key.SetValue("MouseSpeed", DefaultSpeed, RegistryValueKind.String);
            key.SetValue("MouseThreshold1", DefaultThreshold1, RegistryValueKind.String);
            key.SetValue("MouseThreshold2", DefaultThreshold2, RegistryValueKind.String);

            NotifyChange();

            return (true, "已恢复 Windows 默认的鼠标加速。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>按备份的原值写回（系统调优「还原全部改动」用）。</summary>
    public static (bool Ok, string Message) RestoreExact(string speed, string t1, string t2)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MouseKey, writable: true);
            if (key == null) return (false, "打不开鼠标设置项。");

            key.SetValue("MouseSpeed", string.IsNullOrEmpty(speed) ? DefaultSpeed : speed, RegistryValueKind.String);
            key.SetValue("MouseThreshold1", string.IsNullOrEmpty(t1) ? DefaultThreshold1 : t1, RegistryValueKind.String);
            key.SetValue("MouseThreshold2", string.IsNullOrEmpty(t2) ? DefaultThreshold2 : t2, RegistryValueKind.String);

            NotifyChange();
            return (true, "已还原为修改前的鼠标设置。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    private static void NotifyChange()
    {
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        const uint HwndBroadcast = 0xFFFF;

        try
        {
            SendMessageTimeout((IntPtr)HwndBroadcast, WmSettingChange, IntPtr.Zero, "Control Panel\\Mouse",
                SmtoAbortIfHung, 1000, out _);
        }
        catch
        {
            // 通知失败不影响写入本身
        }
    }
}
