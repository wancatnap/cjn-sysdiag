using System.Net;
using System.Text;

namespace SysDiag.Services;

/// <summary>轻量 HTML 文档构建器，供各类报告复用同一套样式。</summary>
public sealed class HtmlDoc
{
    private static readonly string Css = """
        *{box-sizing:border-box;margin:0;padding:0}
        body{background:#F6F7F9;font-family:"Microsoft YaHei UI","微软雅黑",sans-serif;padding:32px;color:#212529}
        .wrap{max-width:1120px;margin:0 auto}
        h1{font-size:22px;font-weight:600;color:#1F2937}
        h2{font-size:15px;font-weight:600;color:#343A40;margin:26px 0 12px}
        .meta{font-size:12px;color:#868E96;margin-top:8px;line-height:1.8}
        .cards{display:flex;gap:12px;flex-wrap:wrap;margin-top:18px}
        .kpi{background:#fff;border:1px solid #E9ECEF;border-radius:10px;padding:14px 16px;min-width:150px;flex:1}
        .kpi .n{font-size:23px;font-weight:600;line-height:1.2}
        .kpi .l{font-size:11px;color:#868E96;margin-top:5px}
        table{width:100%;border-collapse:collapse;background:#fff;border:1px solid #E9ECEF;border-radius:10px;overflow:hidden;font-size:12px}
        th{background:#F8F9FA;text-align:left;padding:9px 12px;font-weight:600;color:#495057;border-bottom:1px solid #E9ECEF}
        td{padding:8px 12px;border-bottom:1px solid #F1F3F5;vertical-align:top;word-break:break-word}
        tr:last-child td{border-bottom:none}
        code{font-family:Consolas,monospace;font-size:11px;color:#495057;word-break:break-all}
        .tag{display:inline-block;padding:1px 7px;border-radius:20px;font-size:11px}
        .t-ok{background:#EBFBEE;color:#2F9E44}
        .t-hint{background:#FFF9DB;color:#9C6B00}
        .t-warn{background:#FFF4E6;color:#E8590C}
        .t-bad{background:#FFE3E3;color:#C92A2A}
        .t-bin{background:#F1F3F5;color:#868E96}
        .mono{font-family:Consolas,monospace;font-size:11px;color:#495057;word-break:break-all}
        .muted{color:#868E96;font-size:11.5px}
        .note{background:#fff;border:1px solid #E9ECEF;border-radius:10px;padding:12px 14px;font-size:12px;color:#495057;line-height:1.75}
        .empty{color:#ADB5BD;font-size:12px;padding:10px 0}
        .verdict{background:#fff;border-left:4px solid #4C6EF5;border-radius:8px;padding:14px 16px;font-size:13px;line-height:1.9;color:#343A40;margin-top:16px}
        .finding{background:#fff;border:1px solid #E9ECEF;border-radius:10px;padding:14px 16px;margin:10px 0}
        .fh{font-size:13px;font-weight:600;color:#212529}
        .fc{font-size:11px;color:#868E96;font-weight:400;margin-left:8px}
        .fm{font-size:12px;color:#495057;margin-top:8px;line-height:1.75}
        .fa{font-size:12px;color:#2B8A3E;margin-top:6px;line-height:1.75}
        .fs{font-family:Consolas,monospace;font-size:10.5px;color:#ADB5BD;margin-top:8px;word-break:break-all;line-height:1.6}
        """;

    private readonly StringBuilder _sb = new();

    public void Raw(string html) => _sb.Append(html ?? "");

    public HtmlDoc(string title, string subtitle)
    {
        _sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"UTF-8\">");
        _sb.Append("<title>").Append(E(title)).Append("</title><style>").Append(Css).Append("</style></head><body><div class=\"wrap\">");
        _sb.Append("<h1>").Append(E(title)).Append("</h1>");
        _sb.Append("<div class=\"meta\">").Append(subtitle ?? "").Append("</div>");
    }

    public static string E(string s) => WebUtility.HtmlEncode(s ?? "");

    public void Kpis(params (string Num, string Label, string Color)[] items)
    {
        if (items.Length == 0) return;
        _sb.Append("<div class=\"cards\">");
        foreach (var (num, label, color) in items)
        {
            _sb.Append("<div class=\"kpi\"><div class=\"n\"");
            if (!string.IsNullOrEmpty(color)) _sb.Append(" style=\"color:").Append(color).Append('"');
            _sb.Append('>').Append(E(num)).Append("</div><div class=\"l\">").Append(E(label)).Append("</div></div>");
        }
        _sb.Append("</div>");
    }

    public void H2(string heading) => _sb.Append("<h2>").Append(E(heading)).Append("</h2>");

    public void Note(string text) => _sb.Append("<div class=\"note\">").Append(text ?? "").Append("</div>");

    public void Empty(string text) => _sb.Append("<div class=\"empty\">").Append(E(text)).Append("</div>");

    public void Table(string[] headers, IEnumerable<string[]> rows, string[] widths = null)
    {
        var list = rows as IList<string[]> ?? rows.ToList();
        if (list.Count == 0) { Empty("（无数据）"); return; }

        _sb.Append("<table><tr>");
        for (var i = 0; i < headers.Length; i++)
        {
            _sb.Append("<th");
            if (widths != null && i < widths.Length && !string.IsNullOrEmpty(widths[i]))
                _sb.Append(" style=\"width:").Append(widths[i]).Append('"');
            _sb.Append('>').Append(E(headers[i])).Append("</th>");
        }
        _sb.Append("</tr>");

        foreach (var row in list)
        {
            _sb.Append("<tr>");
            for (var i = 0; i < headers.Length; i++)
            {
                var cell = i < row.Length ? row[i] : "";
                _sb.Append("<td>").Append(cell ?? "").Append("</td>");
            }
            _sb.Append("</tr>");
        }
        _sb.Append("</table>");
    }

    public string Build() => _sb.Append("</div></body></html>").ToString();
}
