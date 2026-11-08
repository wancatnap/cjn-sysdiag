using System.Windows.Media;
using SysDiag.Services;

namespace SysDiag.Models;

/// <summary>两份报告对比后的一行。</summary>
public sealed class CompareRow
{
    public string Group { get; set; } = "";

    public string Title { get; set; } = "";

    public string Detail { get; set; } = "";

    public string LevelA { get; set; } = "";

    public string LevelB { get; set; } = "";

    /// <summary>worse / improve / added / removed / same</summary>
    public string ChangeKey { get; set; } = "same";

    public string ChangeText => ChangeKey switch
    {
        "worse" => "恶化",
        "improve" => "改善",
        "added" => "新增",
        "removed" => "消失",
        _ => "相同"
    };

    public Brush ChangeBrush => Palette.Get(ChangeKey switch
    {
        "worse" => "#FB7185",
        "improve" => "#34D399",
        "added" => "#FBBF24",
        "removed" => "#63758F",
        _ => "#63758F"
    });
}

/// <summary>对比结果。</summary>
public sealed class CompareResult
{
    public ReportData Left { get; set; }

    public ReportData Right { get; set; }

    public List<CompareRow> Rows { get; set; } = new();

    public int WorseCount => Rows.Count(r => r.ChangeKey == "worse");

    public int ImproveCount => Rows.Count(r => r.ChangeKey == "improve");

    public int AddedCount => Rows.Count(r => r.ChangeKey == "added");

    public int RemovedCount => Rows.Count(r => r.ChangeKey == "removed");

    public int SameCount => Rows.Count(r => r.ChangeKey == "same");

    public int ScoreDelta => Right.Verdict.Score - Left.Verdict.Score;

    public bool HasScore => Left.Verdict.Score >= 0 && Right.Verdict.Score >= 0;

    public string Summary
    {
        get
        {
            if (WorseCount == 0 && ImproveCount == 0 && AddedCount == 0 && RemovedCount == 0)
                return "两份报告的条目完全一致。";

            var parts = new List<string>();

            if (WorseCount > 0) parts.Add($"{WorseCount} 项恶化");
            if (AddedCount > 0) parts.Add($"{AddedCount} 项新增");
            if (ImproveCount > 0) parts.Add($"{ImproveCount} 项改善");
            if (RemovedCount > 0) parts.Add($"{RemovedCount} 项消失");

            return string.Join("，", parts) + $"，{SameCount} 项相同。";
        }
    }

    public string ScoreText
    {
        get
        {
            if (!HasScore) return "这份报告没有风险分";

            var delta = ScoreDelta;
            var arrow = delta > 0 ? "↑" : delta < 0 ? "↓" : "→";

            return $"{Left.Verdict.Score} → {Right.Verdict.Score}　{arrow} {Math.Abs(delta)}";
        }
    }

    public Brush ScoreBrush => (!HasScore || ScoreDelta == 0 ? Palette.InkFaint : ScoreDelta > 0 ? Palette.Danger : Palette.Ok);
}
