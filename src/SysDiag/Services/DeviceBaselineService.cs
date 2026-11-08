using System.IO;
using System.Text;
using System.Text.Json;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 硬件基线：把机器码与 USB 设备存成一份快照，之后比对哪些字段变了。
/// 用于证明「这台机器没换过件」，或者反过来发现硬件/驱动被改过。
/// </summary>
public static class DeviceBaselineService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "baseline.json");

    public static DeviceSnapshot Capture() => new()
    {
        SavedAt = DateTime.Now,
        Computer = Environment.MachineName,
        Os = Environment.OSVersion.VersionString,
        Fields = EvidenceService.CollectHwid(),
        Usb = EvidenceService.ReadUsbDevices()
    };

    public static DeviceSnapshot Save()
    {
        AppPaths.Ensure();
        var snapshot = Capture();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? AppPaths.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, Json), new UTF8Encoding(false));
        return snapshot;
    }

    public static DeviceSnapshot Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<DeviceSnapshot>(File.ReadAllText(FilePath), Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>与已保存的基线比对；没有基线时返回 null。</summary>
    public static BaselineDiff Compare()
    {
        var baseline = Load();
        return baseline == null ? null : Compare(baseline, Capture());
    }

    public static BaselineDiff Compare(DeviceSnapshot baseline, DeviceSnapshot current)
    {
        var diff = new BaselineDiff { Baseline = baseline };

        var oldMap = ToMap(baseline.Fields);
        var newMap = ToMap(current.Fields);

        foreach (var kv in oldMap)
        {
            if (newMap.TryGetValue(kv.Key, out var now))
            {
                if (!string.Equals(kv.Value, now, StringComparison.Ordinal))
                    diff.Changed.Add((kv.Key, kv.Value, now));
                else
                    diff.SameCount++;
            }
            else
            {
                diff.Removed.Add((kv.Key, kv.Value));
            }
        }

        foreach (var kv in newMap)
        {
            if (!oldMap.ContainsKey(kv.Key)) diff.Added.Add((kv.Key, kv.Value));
        }

        var oldUsb = new HashSet<string>(baseline.Usb.Select(UsbKey));
        var newUsb = new HashSet<string>(current.Usb.Select(UsbKey));
        diff.UsbAdded.AddRange(newUsb.Except(oldUsb).OrderBy(x => x));
        diff.UsbRemoved.AddRange(oldUsb.Except(newUsb).OrderBy(x => x));

        return diff;
    }

    private static Dictionary<string, string> ToMap(IEnumerable<HwidField> fields)
        => fields
            .GroupBy(f => $"{f.Group} / {f.Name}")
            .ToDictionary(g => g.Key, g => g.First().Value);

    private static string UsbKey(UsbDeviceInfo d)
        => $"{d.VidPid} · {d.Name}" + (d.Serial.Length > 0 ? $" · {d.Serial}" : "");

    // ==================== 报告 ====================

    public static ReportPayload CompareReport(BaselineDiff diff)
    {
        var html = HtmlReportBuilder.BuildBaselineDiff(diff);
        var path = HtmlReportBuilder.Save(html, "设备比对");

        var sb = new StringBuilder();
        sb.AppendLine("【设备比对】" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine($"基线时间：{diff.Baseline.SavedAt:yyyy-MM-dd HH:mm}　基线机器：{diff.Baseline.Computer}");
        sb.AppendLine($"变化 {diff.TotalChanges} 项（改动 {diff.Changed.Count} / 新增 {diff.Added.Count} / 消失 {diff.Removed.Count} / " +
                      $"USB 新增 {diff.UsbAdded.Count}、消失 {diff.UsbRemoved.Count}），未变 {diff.SameCount} 项。");
        sb.AppendLine();

        if (diff.TotalChanges == 0)
        {
            sb.AppendLine("结论：硬件标识与 USB 设备清单与基线完全一致。");
        }
        else
        {
            foreach (var (key, oldV, newV) in diff.Changed)
                sb.AppendLine($"  [改动] {key}：{oldV}  →  {newV}");
            foreach (var (key, value) in diff.Added)
                sb.AppendLine($"  [新增] {key}：{value}");
            foreach (var (key, value) in diff.Removed)
                sb.AppendLine($"  [消失] {key}：{value}");
            foreach (var u in diff.UsbAdded) sb.AppendLine($"  [USB 新增] {u}");
            foreach (var u in diff.UsbRemoved) sb.AppendLine($"  [USB 消失] {u}");
        }

        return new ReportPayload
        {
            Title = "设备比对",
            Subtitle = $"{diff.TotalChanges} 项变化 · 基线 {diff.Baseline.SavedAt:yyyy-MM-dd HH:mm}",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }
}
