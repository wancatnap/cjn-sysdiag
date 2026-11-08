using System.Management;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 磁盘与卷（「磁盘管理」的软件内只读视图）。
///
/// 只做查看：分区、格式化、改盘符要调用 VDS/Storage 接口并处理卷影与引导记录，
/// 出错代价太大，那部分保留给 diskmgmt.msc。
/// </summary>
public static class DiskInventoryService
{
    public sealed class DiskEntry
    {
        public int Index { get; set; } = -1;
        public string Model { get; set; } = "";
        public string Serial { get; set; } = "";
        public string Interface { get; set; } = "";
        public long SizeBytes { get; set; }
        public int Partitions { get; set; }
        public string Status { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string DeviceId { get; set; } = "";

        public string SizeText => SizeBytes <= 0 ? "" : Format(SizeBytes);
    }

    public sealed class VolumeEntry
    {
        public string Letter { get; set; } = "";
        public string Label { get; set; } = "";
        public string FileSystem { get; set; } = "";
        public long SizeBytes { get; set; }
        public long FreeBytes { get; set; }
        public string DriveType { get; set; } = "";

        public double UsedPercent => SizeBytes <= 0 ? 0 : (SizeBytes - FreeBytes) * 100.0 / SizeBytes;

        public string SizeText => SizeBytes <= 0 ? "" : Format(SizeBytes);

        public string FreeText => SizeBytes <= 0 ? "" : Format(FreeBytes);

        public string Level => UsedPercent >= 92 ? "warn" : UsedPercent >= 80 ? "hint" : "ok";

        public string LevelText => UsedPercent >= 92 ? "空间告急" : UsedPercent >= 80 ? "偏高" : "正常";
    }

    public static (List<DiskEntry> Disks, List<VolumeEntry> Volumes) Scan()
    {
        var disks = new List<DiskEntry>();

        foreach (var d in EvidenceService.Wmi(
                     "SELECT Index, Model, SerialNumber, InterfaceType, Size, Partitions, Status, MediaType, DeviceID " +
                     "FROM Win32_DiskDrive"))
        {
            disks.Add(new DiskEntry
            {
                Index = int.TryParse(EvidenceService.Str(d, "Index"), out var i) ? i : -1,
                Model = EvidenceService.Str(d, "Model").Trim(),
                Serial = EvidenceService.Str(d, "SerialNumber").Trim(),
                Interface = EvidenceService.Str(d, "InterfaceType"),
                SizeBytes = long.TryParse(EvidenceService.Str(d, "Size"), out var size) ? size : 0,
                Partitions = int.TryParse(EvidenceService.Str(d, "Partitions"), out var p) ? p : 0,
                Status = EvidenceService.Str(d, "Status"),
                MediaType = EvidenceService.Str(d, "MediaType"),
                DeviceId = EvidenceService.Str(d, "DeviceID")
            });
        }

        var volumes = new List<VolumeEntry>();

        foreach (var v in EvidenceService.Wmi(
                     "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk"))
        {
            var letter = EvidenceService.Str(v, "DeviceID");
            if (letter.Length == 0) continue;

            var size = long.TryParse(EvidenceService.Str(v, "Size"), out var s) ? s : 0;
            if (size == 0) continue;   // 光驱 / 未挂载卷没有容量

            volumes.Add(new VolumeEntry
            {
                Letter = letter,
                Label = EvidenceService.Str(v, "VolumeName"),
                FileSystem = EvidenceService.Str(v, "FileSystem"),
                SizeBytes = size,
                FreeBytes = long.TryParse(EvidenceService.Str(v, "FreeSpace"), out var f) ? f : 0,
                DriveType = EvidenceService.Str(v, "DriveType") switch
                {
                    "2" => "可移动",
                    "3" => "本地磁盘",
                    "4" => "网络",
                    "5" => "光驱",
                    "6" => "内存盘",
                    _ => "未知"
                }
            });
        }

        return (disks.OrderBy(d => d.Index).ToList(),
            volumes.OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string Format(long bytes) => bytes >= 1L << 40
        ? $"{bytes / 1024.0 / 1024 / 1024 / 1024:0.##} TB"
        : $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
}
