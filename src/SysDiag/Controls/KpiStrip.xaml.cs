using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;

namespace SysDiag.Controls;

/// <summary>KPI 项：数字 + 标签 + 颜色（传 Palette 语义 hex，空则用默认文本色）。</summary>
public sealed class KpiEntry
{
    public string Num { get; init; } = "";
    public string Label { get; init; } = "";
    public Brush Brush { get; init; } = Palette.Ink;
}

/// <summary>KPI 数字行组件：Replace 一次性填整行，UpdateColor 只改某一项颜色。</summary>
public partial class KpiStrip : UserControl
{
    private List<KpiEntry> _items = new();

    public KpiStrip()
    {
        InitializeComponent();
    }

    public int Count => _items.Count;

    /// <summary>填整行。hex 建议传 Palette.WarnHex / Palette.OkHex 等语义常量。</summary>
    public void Set(params (string Num, string Label, string Hex)[] items)
    {
        _items = new List<KpiEntry>(items.Length);
        foreach (var (num, label, hex) in items)
        {
            _items.Add(new KpiEntry
            {
                Num = num,
                Label = label,
                Brush = string.IsNullOrEmpty(hex) ? Palette.Ink : Palette.Get(hex)
            });
        }

        Host.ItemsSource = null;
        Host.ItemsSource = _items;
    }

    /// <summary>只更新第 index 项的数字与颜色，标签不变。</summary>
    public void Update(int index, string num, string hex = "")
    {
        if (index < 0 || index >= _items.Count) return;

        var old = _items[index];
        _items[index] = new KpiEntry
        {
            Num = num,
            Label = old.Label,
            Brush = string.IsNullOrEmpty(hex) ? old.Brush : Palette.Get(hex)
        };

        Host.ItemsSource = null;
        Host.ItemsSource = _items;
    }

    /// <summary>第 index 项当前显示的数字（自检用）。</summary>
    public string NumOf(int index) => index >= 0 && index < _items.Count ? _items[index].Num : "";
}
