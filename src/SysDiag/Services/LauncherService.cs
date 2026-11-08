using System.Diagnostics;
using SysDiag.Models;
using System.IO;

namespace SysDiag.Services;

public static class LauncherService
{
    /// <summary>执行一张卡片。诊断类返回报告，由调用方选择展示方式。</summary>
    public static ReportPayload Invoke(CardItem item)
    {
        switch ((item.Kind ?? "").ToLowerInvariant())
        {
            case "exe":
                Start(item.Target, item.Args);
                return null;
            case "msc":
                Start("mmc.exe", new[] { item.Target });
                return null;
            case "folder":
                OpenFolder(item.Target);
                return null;
            case "shell":
                StartShell(item.Target);
                return null;
            case "diag":
                return RunDiagnostic(item.Target);
            default:
                throw new InvalidOperationException($"未知的卡片类型：{item.Kind}");
        }
    }

    private static void Start(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Arguments = args is { Length: > 0 } ? string.Join(" ", args) : ""
        };
        Process.Start(psi);
    }

    private static void StartShell(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else
            throw new DirectoryNotFoundException($"目录不存在：{path}");
    }

    private static ReportPayload RunDiagnostic(string key) => key switch
    {
        "system-checkup" => DiagnosticsService.SystemCheckup(),
        "error-report" => DiagnosticsService.ErrorReport(),
        "disk-health" => DiagnosticsService.DiskHealth(),
        "network-report" => DiagnosticsService.NetworkReport(),
        "update-list" => DiagnosticsService.UpdateList(),
        "hwid" => EvidenceService.HwidReport(),
        "driver-history" => EvidenceService.DriverHistoryReport(),
        "usb-history" => EvidenceService.UsbHistoryReport(),
        "pnp-report" => EvidenceService.PnpReport(),
        "evidence-pack" => EvidenceService.EvidencePackReport(),
        _ => throw new InvalidOperationException($"未知的诊断项：{key}")
    };

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
