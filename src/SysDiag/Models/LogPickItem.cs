using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>「选择要分析的文件」列表里的一行。</summary>
public sealed class LogPickItem : INotifyPropertyChanged
{
    private bool _selected;

    public string Path { get; set; } = "";
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public bool IsAnalyzable { get; set; } = true;
    public bool IsEventLog { get; set; }

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

    public string Name => RelPath;

    public string SizeText => LogFileItem.FormatSize(Size);

    public string TimeText => Modified == DateTime.MinValue
        ? ""
        : Modified.ToString("yyyy-MM-dd HH:mm");

    public string KindText => IsEventLog ? "事件日志" : IsAnalyzable ? "文本" : "非文本";

    public Brush KindBrush => (IsAnalyzable ? Palette.Ok : Palette.None);

    /// <summary>非文本文件会被跳过，提示用户别勾。</summary>
    public string WarnText => IsAnalyzable ? "" : "分析时会跳过";

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
