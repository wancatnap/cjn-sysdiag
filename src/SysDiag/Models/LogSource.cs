using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

public enum LogSourceKind
{
    Zip,
    Folder,
    Files
}

/// <summary>一次分析的输入：一个 zip、一个目录，或若干已选文件。</summary>
public sealed class LogSource
{
    public LogSourceKind Kind { get; set; } = LogSourceKind.Zip;
    public string Label { get; set; } = "";
    public List<string> Paths { get; set; } = new();

    /// <summary>这些文件来自哪个目录（用户从该目录里自选文件时使用），用于报告标题与“最近分析”记忆。</summary>
    public string Root { get; set; } = "";

    public static LogSource FromZip(string path) => new()
    {
        Kind = LogSourceKind.Zip,
        Label = Path.GetFileName(path),
        Paths = new List<string> { SafeFullPath(path) }
    };

    public static LogSource FromFolder(string path) => new()
    {
        Kind = LogSourceKind.Folder,
        Label = SafeFullPath(path),
        Paths = new List<string> { SafeFullPath(path) }
    };

    public static LogSource FromFiles(IEnumerable<string> paths)
    {
        var list = paths.Select(SafeFullPath).ToList();
        return new LogSource
        {
            Kind = LogSourceKind.Files,
            Label = list.Count == 1 ? Path.GetFileName(list[0]) : $"{list.Count} 个文件",
            Paths = list
        };
    }

    /// <summary>用户从一个目录里自己勾选了若干文件：保留所属目录，报告标题按目录命名。</summary>
    public static LogSource FromPickedFiles(string root, IEnumerable<string> paths)
    {
        var list = paths.Select(SafeFullPath).ToList();
        return new LogSource
        {
            Kind = LogSourceKind.Files,
            Label = list.Count == 1 ? Path.GetFileName(list[0]) : $"{list.Count} 个文件",
            Paths = list,
            Root = string.IsNullOrEmpty(root) ? "" : SafeFullPath(root)
        };
    }

    /// <summary>统一成规范化的绝对路径，避免展示时出现混合斜杠。</summary>
    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}

public enum CandidateKind
{
    Safe,       // 用户权限即可清理
    NeedsAdmin, // 需要管理员
    ReadOnly    // 只展示，不提供删除
}

/// <summary>清理清单里的单个文件（也可能是一个目录，如工具自己的解压缓存）。</summary>
public sealed class LogFileItem : INotifyPropertyChanged
{
    private bool _selected;

    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool IsDirectory { get; set; }

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

    public string SizeText => FormatSize(Size);

    public static string FormatSize(long size) => size switch
    {
        >= 1024L * 1024 * 1024 => $"{size / 1024.0 / 1024 / 1024:N2} GB",
        >= 1024 * 1024 => $"{size / 1024.0 / 1024:N1} MB",
        >= 1024 => $"{size / 1024.0:N1} KB",
        _ => $"{size} B"
    };

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>可清理的一组日志（同一位置下的若干文件），支持整组或逐个勾选。</summary>
public sealed class LogCandidate : INotifyPropertyChanged
{
    private bool? _groupSelected;
    private bool _suspend;
    private bool _isExpanded;

    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayPath { get; set; } = "";
    public CandidateKind Kind { get; set; } = CandidateKind.Safe;
    public ObservableCollection<LogFileItem> Files { get; } = new();

    public long Size => Files.Sum(f => f.Size);
    public int Count => Files.Count;
    public bool CanDelete => Kind != CandidateKind.ReadOnly && Files.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); }
    }

    public void SetFiles(IEnumerable<LogFileItem> files)
    {
        foreach (var f in Files) f.PropertyChanged -= OnFileChanged;
        Files.Clear();

        foreach (var f in files)
        {
            f.PropertyChanged += OnFileChanged;
            Files.Add(f);
        }

        IsExpanded = Files.Count <= 6;
        SyncGroupState();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SizeText));
    }

    private void OnFileChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogFileItem.Selected)) SyncGroupState();
    }

    private void SyncGroupState()
    {
        if (_suspend) return;

        var selected = Files.Count(f => f.Selected);
        bool? state = selected == 0 ? false
            : selected == Files.Count ? true
            : null;

        if (_groupSelected != state)
        {
            _groupSelected = state;
            OnPropertyChanged(nameof(GroupSelected));
            OnPropertyChanged(nameof(SelectedText));
        }
    }

    /// <summary>三态：true 全选 / false 全不选 / null 部分选中。</summary>
    public bool? GroupSelected
    {
        get => _groupSelected;
        set
        {
            if (_suspend) return;
            if (value == null) return; // 中间态由界面自行处理

            _suspend = true;
            foreach (var f in Files) f.Selected = value.Value;
            _suspend = false;

            _groupSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedText));
        }
    }

    public string SelectedText
    {
        get
        {
            var n = Files.Count(f => f.Selected);
            return n == 0 ? "" : $"已选 {n}/{Files.Count}";
        }
    }

    public string SizeText => LogFileItem.FormatSize(Size);
    public string CountText => Count > 1 ? $"{Count} 个文件" : "1 项";

    public string KindText => Kind switch
    {
        CandidateKind.Safe => "可直接清理",
        CandidateKind.NeedsAdmin => "需要管理员",
        _ => "仅展示"
    };

    public Brush KindBrush => Kind switch
    {
        CandidateKind.Safe => Palette.LevelBrush("ok"),
        CandidateKind.NeedsAdmin => Palette.HintChip,
        _ => Palette.None
    };

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
