using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>注册表清理的风险档：安全 = 指向的目标已经不存在；谨慎 = 目标不存在但可能有别的用途。</summary>
public enum RegRisk
{
    Safe,
    Careful
}

/// <summary>一条「孤儿」注册表项：指向的文件/目录已经不在了。</summary>
public sealed class RegistryFinding : INotifyPropertyChanged
{
    /// <summary>完整键路径（用 <c>\</c> 分隔，形如 <c>HKCU\Software\...\Run</c>）。</summary>
    public string KeyPath { get; init; } = "";

    /// <summary>要删的值名；为空表示整条子键都该删（卸载项）。</summary>
    public string ValueName { get; init; } = "";

    /// <summary>面向人的名字（值名或子键名）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>分类：启动项 / 卸载项 / App Paths / 共享 DLL / MUI 缓存。</summary>
    public string Category { get; init; } = "";

    /// <summary>证据：它指向的、已经不存在的东西。</summary>
    public string Evidence { get; init; } = "";

    /// <summary>这条记录的原始数据（启动项的命令行、卸载项的卸载串等）。</summary>
    public string Raw { get; init; } = "";

    public RegRisk Risk { get; init; } = RegRisk.Safe;

    /// <summary>是否是整条子键删除（而不是删一个值）。</summary>
    public bool IsKey => ValueName.Length == 0;

    private bool _selected;

    /// <summary>默认一律不勾：注册表清理不做「默认推荐」，逐条看明白再动手。</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
        }
    }

    public string LevelText => Risk == RegRisk.Safe ? "安全" : "谨慎";

    public string LevelBrush => Palette.LevelChipHex(Risk == RegRisk.Safe ? "ok" : "hint");

    public string PathText => IsKey ? KeyPath : $"{KeyPath} → {ValueName}";

    /// <summary>删除动作的可读描述（报告与确认框用）。</summary>
    public string ActionText => IsKey ? $"删除子键 {KeyPath}" : $"删除值 {KeyPath}\\{ValueName}";

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一次注册表扫描的结果。</summary>
public sealed class RegistryScanResult
{
    public List<RegistryFinding> Findings { get; init; } = new();

    /// <summary>扫了但没法判断的条数（例如 MSI 卸载项、命令里没有绝对路径的启动项）。</summary>
    public int Skipped { get; init; }

    public string ElapsedText { get; init; } = "";

    public int SafeCount => Findings.Count(f => f.Risk == RegRisk.Safe);

    public int CarefulCount => Findings.Count(f => f.Risk == RegRisk.Careful);

    public int SelectedCount => Findings.Count(f => f.Selected);

    public string Summary => Findings.Count == 0
        ? "没有发现指向失效目标的注册表项"
        : $"发现 {Findings.Count} 条孤儿项（安全 {SafeCount} / 谨慎 {CarefulCount}）" +
          (Skipped > 0 ? $" · 另有 {Skipped} 条无法判断已跳过" : "");
}

/// <summary>清理结果。</summary>
public sealed class RegistryCleanOutcome
{
    public int Removed { get; set; }

    public int Failed { get; set; }

    /// <summary>备份目录（清理前用 reg export 导出的 .reg 文件都在这儿）。</summary>
    public string BackupDir { get; set; } = "";

    public List<string> Errors { get; init; } = new();

    public string Summary
    {
        get
        {
            if (Removed == 0 && Failed == 0) return "没有删除任何项";
            if (Failed == 0) return $"已删除 {Removed} 项，备份在 {BackupDir}";
            return $"已删除 {Removed} 项，失败 {Failed} 项" + (Errors.Count > 0 ? $"（{Errors[0]}）" : "");
        }
    }
}
