using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 报告对比：导入一到两份 .sdreport。
/// 一份 → 浏览内容；两份 → 自动对比并标出恶化/改善/新增/消失。
/// </summary>
public partial class ReportCompareWindow : Window
{
    private readonly ObservableCollection<CompareRow> _rows = new();

    private ReportPackage.ImportResult _left;
    private ReportPackage.ImportResult _right;
    private CompareResult _compare;

    /// <summary>自检读取：已加载的报告数。</summary>
    public int LoadedCount => (_left == null ? 0 : 1) + (_right == null ? 0 : 1);

    public CompareResult Comparison => _compare;

    public int RowCount => _rows.Count;

    public ReportCompareWindow()
    {
        InitializeComponent();

        CompareList.ItemsSource = _rows;

        Loaded += (_, _) => UpdateSlots();
    }

    /// <summary>供自检直接加载文件。</summary>
    public void LoadLeft(string path)
    {
        _left = ReportPackage.Import(path);
        Refresh();
    }

    public void LoadRight(string path)
    {
        _right = ReportPackage.Import(path);
        Refresh();
    }

    private void OnPickA(object sender, RoutedEventArgs e) => Pick(true);

    private void OnPickB(object sender, RoutedEventArgs e) => Pick(false);

    private void Pick(bool isLeft)
    {
        var dlg = new OpenFileDialog
        {
            Title = isLeft ? "选择基准报告（A）" : "选择对比报告（B）",
            Filter = $"报告包 (*{ReportPackage.Extension})|*{ReportPackage.Extension}|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            if (isLeft) LoadLeft(dlg.FileName);
            else LoadRight(dlg.FileName);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "导入失败：\n\n" + ex.Message, "报告对比",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0) return;

        var path = files.FirstOrDefault(f => f.EndsWith(ReportPackage.Extension, StringComparison.OrdinalIgnoreCase));
        if (path == null)
        {
            HintText.Text = $"请拖入 {ReportPackage.Extension} 文件。";
            return;
        }

        try
        {
            if (_left == null) LoadLeft(path);
            else if (_right == null) LoadRight(path);
            else LoadRight(path);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "导入失败：\n\n" + ex.Message, "报告对比",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Refresh()
    {
        UpdateSlots();

        var leftData = _left?.Data;
        var rightData = _right?.Data;

        if (leftData != null && rightData != null && leftData.IsCompatible() && rightData.IsCompatible())
        {
            _compare = ReportCompareService.Compare(leftData, rightData);

            _rows.Clear();
            foreach (var row in _compare.Rows) _rows.Add(row);

            ComparePanel.Visibility = Visibility.Visible;
            SinglePanel.Visibility = Visibility.Collapsed;
            ExportBtn.IsEnabled = true;

            SummaryText.Text = _compare.Summary;
            ScoreText.Text = "风险分：" + _compare.ScoreText +
                             $"　·　A {leftData.ItemCount} 项 / B {rightData.ItemCount} 项";
            HintText.Text = $"对比完成：{_compare.WorseCount} 项恶化，{_compare.AddedCount} 项新增";

            return;
        }

        _compare = null;
        ExportBtn.IsEnabled = false;
        ComparePanel.Visibility = Visibility.Collapsed;
        SinglePanel.Visibility = Visibility.Visible;

        var shown = rightData ?? leftData;

        if (shown != null)
        {
            SingleList.ItemsSource = shown.Sections;

            SummaryText.Text = string.IsNullOrEmpty(shown.Verdict.Text) ? "已导入一份报告" : shown.Verdict.Text;
            ScoreText.Text = $"机器：{shown.Machine.Name}　·　{shown.GeneratedText}　·　{shown.ItemCount} 项" +
                             (shown.Verdict.Score >= 0 ? $"　·　风险分 {shown.Verdict.Score}" : "");
            HintText.Text = "再导入一份即可对比";
        }
        else
        {
            SingleList.ItemsSource = null;
            SummaryText.Text = "导入一份报告即可查看；导入两份自动对比。";
            ScoreText.Text = "";
            HintText.Text = "等待导入…";
        }
    }

    private void UpdateSlots()
    {
        SlotAFile.Text = _left == null ? "点击选择 .sdreport，或拖入" : _left.SourceName;
        SlotAMeta.Text = _left == null
            ? ""
            : $"{_left.Data.Machine.Name}　·　{_left.Data.GeneratedText}　·　{_left.Data.ItemCount} 项" +
              (_left.Data.Verdict.Score >= 0 ? $"　·　风险分 {_left.Data.Verdict.Score}" : "") +
              (_left.Note.Length > 0 ? $"\n⚠ {_left.Note}" : "");

        SlotBFile.Text = _right == null ? "点击选择 .sdreport，或拖入" : _right.SourceName;
        SlotBMeta.Text = _right == null
            ? ""
            : $"{_right.Data.Machine.Name}　·　{_right.Data.GeneratedText}　·　{_right.Data.ItemCount} 项" +
              (_right.Data.Verdict.Score >= 0 ? $"　·　风险分 {_right.Data.Verdict.Score}" : "") +
              (_right.Note.Length > 0 ? $"\n⚠ {_right.Note}" : "");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _left = null;
        _right = null;
        _compare = null;
        _rows.Clear();

        Refresh();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_compare == null) return;

        var payload = ReportCompareService.BuildPayload(_compare);
        var dlg = new SaveFileDialog
        {
            Title = "导出对比报告",
            Filter = $"报告包 (*{ReportPackage.Extension})|*{ReportPackage.Extension}",
            FileName = $"报告对比-{DateTime.Now:yyyyMMdd-HHmm}{ReportPackage.Extension}",
            InitialDirectory = AppPaths.OutputDir
        };

        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var path = ReportPackage.Export(payload, dlg.FileName);
            HintText.Text = "已导出：" + Path.GetFileName(path);

            UiDialog.Show(this, $"对比报告已导出：\n{path}\n\n可以直接发给别人，对方用「报告对比」导入即可查看。",
                "报告对比", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HintText.Text = "导出失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
