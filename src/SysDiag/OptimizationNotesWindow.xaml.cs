using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 优化推论窗口：只看结论与依据，不执行任何系统改动。
/// 目的是把「为什么」讲清楚，避免用户对着开关盲点。
/// </summary>
public partial class OptimizationNotesWindow : Window
{
    private NoteModule? _filter;
    private string _keyword = "";

    public OptimizationNotesWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        BoundaryText.Text =
            "延迟优化：" + ModuleBoundary.LatencyCriterion + "\n" +
            "系统优化：" + ModuleBoundary.SystemCriterion + "\n" +
            "免重装精简：" + ModuleBoundary.DebloatCriterion;

        Render();
        UpdateFilterButtons();
    }

    /// <summary>自检用：当前渲染出的条目数。</summary>
    public int NoteCount { get; private set; }

    // ---------------------------------------------------------------- 筛选

    private void OnFilterAll(object sender, RoutedEventArgs e) => SetFilter(null);
    private void OnFilterLatency(object sender, RoutedEventArgs e) => SetFilter(NoteModule.Latency);
    private void OnFilterSystem(object sender, RoutedEventArgs e) => SetFilter(NoteModule.System);
    private void OnFilterDebloat(object sender, RoutedEventArgs e) => SetFilter(NoteModule.Debloat);

    private void SetFilter(NoteModule? module)
    {
        _filter = module;
        Render();
        UpdateFilterButtons();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _keyword = SearchBox.Text.Trim();
        Render();
    }

    private void UpdateFilterButtons()
    {
        Highlight(AllBtn, _filter == null);
        Highlight(LatencyBtn, _filter == NoteModule.Latency);
        Highlight(SystemBtn, _filter == NoteModule.System);
        Highlight(DebloatBtn, _filter == NoteModule.Debloat);
    }

    private static void Highlight(Button button, bool active)
    {
        button.Background = active ? Palette.PrimarySoft : Palette.Surface;
        button.Foreground = active ? Palette.Primary : Palette.InkSecondary;
        button.BorderBrush = active ? Palette.Primary : Palette.LineBright;
    }

    // ---------------------------------------------------------------- 渲染

    private void Render()
    {
        var notes = OptimizationNoteCatalog.All.AsEnumerable();

        if (_filter != null)
            notes = notes.Where(n => n.Module == _filter);

        if (_keyword.Length > 0)
        {
            notes = notes.Where(n =>
                n.Title.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                n.Conclusion.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                n.Premise.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                n.Basis.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                n.Scope.Contains(_keyword, StringComparison.OrdinalIgnoreCase) ||
                n.Limits.Contains(_keyword, StringComparison.OrdinalIgnoreCase));
        }

        var list = notes.ToList();
        NoteCount = list.Count;

        NoteHost.Children.Clear();
        CountText.Text = $"共 {list.Count} 条";

        if (list.Count == 0)
        {
            NoteHost.Children.Add(new TextBlock
            {
                Text = "没有匹配的条目。换个关键词，或点「全部」看看。",
                FontSize = 11,
                Foreground = Palette.InkFaint,
                Margin = new Thickness(4, 16, 0, 0)
            });
            return;
        }

        // 按模块分组显示，避免延迟和系统优化混在一起
        foreach (var group in list.GroupBy(n => n.Module))
        {
            NoteHost.Children.Add(new TextBlock
            {
                Text = ModuleTitle(group.Key) + $"（{group.Count()}）",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Ink,
                Margin = new Thickness(2, 12, 0, 8)
            });

            foreach (var note in group)
                NoteHost.Children.Add(BuildCard(note));
        }
    }

    private static string ModuleTitle(NoteModule module) => module switch
    {
        NoteModule.Latency => "延迟优化",
        NoteModule.System => "系统优化",
        NoteModule.Debloat => "免重装精简",
        _ => "其他"
    };

    private Border BuildCard(OptimizationNote note)
    {
        var body = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = note.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Ink,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 720,
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(Chip(note.ConfidenceText, ConfidenceBrush(note.Confidence),
            ConfidenceSoft(note.Confidence)));

        // 规模标签只在「这条结论基于测量」时出现。
        // 「单机实测」是提醒：方向可参考，绝对值别外推；「多源一致」才敢当通用结论用。
        if (note.ScopeText.Length > 0)
        {
            head.Children.Add(Chip(note.ScopeText,
                note.EvidenceScope == NoteScope.SingleMachine ? Palette.Warn : Palette.InkSecondary,
                note.EvidenceScope == NoteScope.SingleMachine ? Palette.WarnSoft : Palette.SurfaceRaise));
        }

        body.Children.Add(head);

        body.Children.Add(Field("结论", note.Conclusion, Palette.InkSecondary));
        if (note.Premise.Length > 0) body.Children.Add(Field("前提", note.Premise, Palette.InkSecondary));
        if (note.Basis.Length > 0) body.Children.Add(Field("依据", note.Basis, Palette.InkSecondary));
        if (note.Scope.Length > 0) body.Children.Add(Field("适用场景", note.Scope, Palette.InkSecondary));
        if (note.Limits.Length > 0) body.Children.Add(Field("限制", note.Limits, Palette.Warn));

        if (note.Sources.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "来源：" + string.Join("　·　", note.Sources),
                FontSize = 9.5,
                Foreground = Palette.InkFaint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        return new Border
        {
            Background = Palette.Surface,
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 0, 0, 9),
            Child = body
        };
    }

    private static StackPanel Field(string label, string text, Brush color)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9.5,
            Foreground = Palette.InkFaint
        });

        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = color,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(0, 2, 0, 0)
        });

        return panel;
    }

    private static Border Chip(string text, Brush foreground, Brush background)
    {
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                Foreground = foreground
            }
        };
    }

    private static Brush ConfidenceBrush(NoteConfidence c) => c switch
    {
        NoteConfidence.Measured => Palette.Ok,
        NoteConfidence.Documented => Palette.Primary,
        NoteConfidence.MultiSource => Palette.Primary,
        _ => Palette.Warn
    };

    private static Brush ConfidenceSoft(NoteConfidence c) => c switch
    {
        NoteConfidence.Measured => Palette.OkSoft,
        NoteConfidence.Documented => Palette.PrimarySoft,
        NoteConfidence.MultiSource => Palette.PrimarySoft,
        _ => Palette.WarnSoft
    };
}
