using System.Net;
using System.Text;
using DiffPlex.DiffBuilder.Model;

namespace SiteComparator;

/// <summary>
/// Renders the comparison outcome as a concise console summary and a self-contained
/// HTML report (inline CSS, native &lt;details&gt; collapsing, no external assets).
/// </summary>
public static class ReportGenerator
{
    private const int MaxDiffRowsPerPage = 800;
    private const int ContextLines = 3;

    public static void WriteConsoleSummary(ComparisonResult r)
    {
        var o = r.Options;
        void W(string s = "") => Console.WriteLine(s);

        W();
        W("====================  SITE COMPARISON  ====================");
        W($"  OLD: {o.OldBaseUrl}   ({r.OldPageCount} pages)");
        W($"  NEW: {o.NewBaseUrl}   ({r.NewPageCount} pages)");
        W("-----------------------------------------------------------");
        W($"  Compared (on both sites) : {r.Compared.Count}");
        W($"    identical              : {r.IdenticalCount}");
        W($"    near-identical (>={o.Threshold:0.#}%) : {r.NearIdenticalCount}");
        W($"    different (<{o.Threshold:0.#}%)        : {r.DifferentCount}");
        W($"  Regressions to review    : {r.RegressionCount}");
        W($"  Only on OLD              : {r.OnlyOld.Count}");
        W($"  Only on NEW              : {r.OnlyNew.Count}");
        W($"  Error pages  OLD / NEW   : {r.OldErrorPages.Count} / {r.NewErrorPages.Count}");
        W("===========================================================");

        if (r.RegressionCount > 0)
        {
            W();
            W("Top regressions:");
            foreach (var c in r.Compared.Where(c => c.IsRegression).Take(15))
            {
                W($"  {c.Similarity,5:0.0}%  {c.Key}");
                foreach (var reason in c.RegressionReasons)
                {
                    W($"           - {reason}");
                }
            }
        }
        W();
    }

    public static string WriteHtmlReport(ComparisonResult r, string path)
    {
        var html = BuildHtml(r);
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return Path.GetFullPath(path);
    }

    private static string BuildHtml(ComparisonResult r)
    {
        var o = r.Options;
        var sb = new StringBuilder();

        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>Site comparison report</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        sb.Append("<h1>Site comparison report</h1>");
        sb.Append("<p class=\"meta\">Generated ").Append(E(DateTime.Now.ToString("yyyy-MM-dd HH:mm")))
          .Append("<br>OLD: <code>").Append(E(o.OldBaseUrl.ToString())).Append("</code>")
          .Append(" &nbsp;&rarr;&nbsp; NEW: <code>").Append(E(o.NewBaseUrl.ToString())).Append("</code></p>");

        AppendSummaryCards(sb, r);
        AppendDifferences(sb, r);
        AppendOnlyList(sb, "Only on OLD site", "Pages reachable on the old site but not the new one — removed pages, or just the stale-database gap.", r.OnlyOld);
        AppendOnlyList(sb, "Only on NEW site", "Pages on the new site with no old-site counterpart — expected for content added since the old database snapshot.", r.OnlyNew);
        AppendErrorPages(sb, "Flagged errors — NEW site", r.NewErrorPages);
        AppendErrorPages(sb, "Flagged errors — OLD site", r.OldErrorPages);
        AppendMatchingList(sb, r);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendSummaryCards(StringBuilder sb, ComparisonResult r)
    {
        var o = r.Options;
        sb.Append("<div class=\"cards\">");
        Card(sb, r.RegressionCount.ToString(), "regressions to review", r.RegressionCount > 0 ? "bad" : "good");
        Card(sb, r.Compared.Count.ToString(), "pages compared", "");
        Card(sb, r.IdenticalCount.ToString(), "identical", "good");
        Card(sb, r.NearIdenticalCount.ToString(), $"near-identical (≥{o.Threshold:0.#}%)", "");
        Card(sb, r.DifferentCount.ToString(), $"different (<{o.Threshold:0.#}%)", r.DifferentCount > 0 ? "warn" : "");
        Card(sb, r.OnlyOld.Count.ToString(), "only on OLD", "");
        Card(sb, r.OnlyNew.Count.ToString(), "only on NEW", "");
        Card(sb, $"{r.NewErrorPages.Count}", "error pages (NEW)", r.NewErrorPages.Count > 0 ? "bad" : "good");
        sb.Append("</div>");
    }

    private static void Card(StringBuilder sb, string value, string label, string cls)
    {
        sb.Append("<div class=\"card ").Append(cls).Append("\"><div class=\"num\">")
          .Append(E(value)).Append("</div><div class=\"lbl\">").Append(E(label)).Append("</div></div>");
    }

    private static void AppendDifferences(StringBuilder sb, ComparisonResult r)
    {
        var diffs = r.Compared.Where(c => c.Verdict != Verdict.Identical).ToList();
        sb.Append("<h2>Differences &amp; regressions <span class=\"count\">")
          .Append(diffs.Count).Append("</span></h2>");

        if (diffs.Count == 0)
        {
            sb.Append("<p class=\"empty\">Every shared page is byte-identical after normalization. 🎉</p>");
            return;
        }

        foreach (var c in diffs)
        {
            var sevClass = c.IsRegression ? "reg" : "minor";
            sb.Append("<details class=\"page ").Append(sevClass).Append("\"><summary>");
            sb.Append("<span class=\"sim\">").Append(c.Similarity.ToString("0.0")).Append("%</span> ");
            if (c.IsRegression) sb.Append("<span class=\"badge bad\">REGRESSION</span> ");
            else sb.Append("<span class=\"badge\">minor</span> ");
            sb.Append("<span class=\"key\">").Append(E(c.Key)).Append("</span>");
            sb.Append("<span class=\"status\">").Append(c.OldStatus).Append('→').Append(c.NewStatus).Append("</span>");
            sb.Append("</summary>");

            sb.Append("<div class=\"detail\">");
            sb.Append("<p class=\"links\">")
              .Append("<a href=\"").Append(E(c.OldUrl)).Append("\" target=\"_blank\" rel=\"noopener\">old</a> &middot; ")
              .Append("<a href=\"").Append(E(c.NewUrl)).Append("\" target=\"_blank\" rel=\"noopener\">new</a>")
              .Append(" &nbsp; text similarity ").Append(c.TextSimilarity.ToString("0.0")).Append('%').Append("</p>");

            if (c.RegressionReasons.Count > 0)
            {
                sb.Append("<ul class=\"reasons\">");
                foreach (var reason in c.RegressionReasons) sb.Append("<li>").Append(E(reason)).Append("</li>");
                sb.Append("</ul>");
            }

            AppendErrorBadges(sb, "errors on new", c.NewErrors);

            if (c.Diff is not null)
            {
                AppendDiffTable(sb, c.Diff);
            }
            sb.Append("</div></details>");
        }
    }

    private static void AppendDiffTable(StringBuilder sb, SideBySideDiffModel model)
    {
        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        var n = Math.Max(oldLines.Count, newLines.Count);

        // Which rows actually changed?
        var changed = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var ot = i < oldLines.Count ? oldLines[i].Type : ChangeType.Imaginary;
            var nt = i < newLines.Count ? newLines[i].Type : ChangeType.Imaginary;
            changed[i] = !(ot == ChangeType.Unchanged && nt == ChangeType.Unchanged);
        }

        // Keep changed rows plus a few lines of context around them.
        var keep = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (!changed[i]) continue;
            for (var j = Math.Max(0, i - ContextLines); j <= Math.Min(n - 1, i + ContextLines); j++)
            {
                keep[j] = true;
            }
        }

        sb.Append("<table class=\"diff\"><tbody>");
        var rendered = 0;
        var i2 = 0;
        while (i2 < n)
        {
            if (!keep[i2])
            {
                var start = i2;
                while (i2 < n && !keep[i2]) i2++;
                sb.Append("<tr class=\"skip\"><td colspan=\"4\">⋯ ").Append(i2 - start)
                  .Append(" unchanged line(s)</td></tr>");
                continue;
            }

            if (rendered >= MaxDiffRowsPerPage)
            {
                sb.Append("<tr class=\"skip\"><td colspan=\"4\">diff truncated — ")
                  .Append(n - i2).Append(" more row(s)</td></tr>");
                break;
            }

            AppendDiffRow(sb,
                i2 < oldLines.Count ? oldLines[i2] : null,
                i2 < newLines.Count ? newLines[i2] : null);
            rendered++;
            i2++;
        }
        sb.Append("</tbody></table>");
    }

    private static void AppendDiffRow(StringBuilder sb, DiffPiece? oldLine, DiffPiece? newLine)
    {
        sb.Append("<tr>");
        AppendDiffCell(sb, oldLine);
        AppendDiffCell(sb, newLine);
        sb.Append("</tr>");
    }

    private static void AppendDiffCell(StringBuilder sb, DiffPiece? line)
    {
        var type = line?.Type ?? ChangeType.Imaginary;
        var cls = type switch
        {
            ChangeType.Inserted => "ins",
            ChangeType.Deleted => "del",
            ChangeType.Modified => "mod",
            ChangeType.Imaginary => "imag",
            _ => "unch",
        };
        var num = line?.Position?.ToString() ?? "";
        sb.Append("<td class=\"ln ").Append(cls).Append("\">").Append(num).Append("</td>");
        sb.Append("<td class=\"tx ").Append(cls).Append("\">").Append(E(line?.Text ?? "")).Append("</td>");
    }

    private static void AppendOnlyList(StringBuilder sb, string title, string blurb, IReadOnlyList<PageResult> pages)
    {
        sb.Append("<h2>").Append(E(title)).Append(" <span class=\"count\">").Append(pages.Count).Append("</span></h2>");
        sb.Append("<p class=\"meta\">").Append(E(blurb)).Append("</p>");
        if (pages.Count == 0)
        {
            sb.Append("<p class=\"empty\">None.</p>");
            return;
        }

        sb.Append("<table class=\"list\"><thead><tr><th>page</th><th>status</th></tr></thead><tbody>");
        foreach (var p in pages)
        {
            sb.Append("<tr><td><a href=\"").Append(E(p.AbsoluteUrl)).Append("\" target=\"_blank\" rel=\"noopener\">")
              .Append(E(p.Key)).Append("</a></td><td>").Append(p.Fetched ? p.StatusCode.ToString() : "ERR").Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static void AppendErrorPages(StringBuilder sb, string title, IReadOnlyList<FlaggedPage> pages)
    {
        sb.Append("<h2>").Append(E(title)).Append(" <span class=\"count\">").Append(pages.Count).Append("</span></h2>");
        if (pages.Count == 0)
        {
            sb.Append("<p class=\"empty\">No error signatures detected.</p>");
            return;
        }

        sb.Append("<table class=\"list\"><thead><tr><th>page</th><th>flags</th></tr></thead><tbody>");
        foreach (var fp in pages)
        {
            sb.Append("<tr><td><a href=\"").Append(E(fp.Page.AbsoluteUrl)).Append("\" target=\"_blank\" rel=\"noopener\">")
              .Append(E(fp.Page.Key)).Append("</a></td><td>");
            foreach (var flag in fp.Flags)
            {
                sb.Append("<span class=\"badge bad\">").Append(E(flag.Category)).Append("</span> ")
                  .Append("<span class=\"flagdetail\">").Append(E(flag.Detail)).Append("</span><br>");
            }
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static void AppendErrorBadges(StringBuilder sb, string label, IReadOnlyList<ErrorFlag> flags)
    {
        if (flags.Count == 0) return;
        sb.Append("<p class=\"errline\">").Append(E(label)).Append(": ");
        foreach (var f in flags)
        {
            sb.Append("<span class=\"badge bad\">").Append(E(f.Category)).Append("</span> ");
        }
        sb.Append("</p>");
    }

    private static void AppendMatchingList(StringBuilder sb, ComparisonResult r)
    {
        var matching = r.Compared.Where(c => c.Verdict == Verdict.Identical).ToList();
        sb.Append("<details class=\"matching\"><summary><h2 style=\"display:inline\">Identical pages <span class=\"count\">")
          .Append(matching.Count).Append("</span></h2></summary>");
        sb.Append("<table class=\"list\"><tbody>");
        foreach (var c in matching)
        {
            sb.Append("<tr><td>").Append(E(c.Key)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></details>");
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root { color-scheme: light dark; }
        body { font: 14px/1.5 system-ui, sans-serif; margin: 0 auto; max-width: 1200px; padding: 24px; }
        h1 { margin: 0 0 4px; }
        h2 { margin: 32px 0 8px; border-bottom: 1px solid #8884; padding-bottom: 4px; }
        .meta { color: #888; font-size: 13px; }
        code { background: #8882; padding: 1px 5px; border-radius: 4px; }
        .count { color: #888; font-weight: normal; font-size: .8em; }
        .empty { color: #888; font-style: italic; }
        .cards { display: flex; flex-wrap: wrap; gap: 12px; margin: 16px 0; }
        .card { flex: 1 1 120px; border: 1px solid #8884; border-radius: 8px; padding: 12px; text-align: center; }
        .card .num { font-size: 28px; font-weight: 700; }
        .card .lbl { font-size: 12px; color: #888; }
        .card.bad .num { color: #d23; } .card.good .num { color: #1a9; } .card.warn .num { color: #e80; }
        details.page { border: 1px solid #8884; border-radius: 6px; margin: 6px 0; }
        details.page summary { cursor: pointer; padding: 8px 12px; display: flex; gap: 10px; align-items: center; }
        details.page.reg summary { background: #d2333312; }
        .sim { font-variant-numeric: tabular-nums; font-weight: 700; min-width: 56px; }
        .key { flex: 1; font-family: ui-monospace, monospace; word-break: break-all; }
        .status { color: #888; font-family: ui-monospace, monospace; }
        .badge { background: #8883; border-radius: 4px; padding: 1px 6px; font-size: 11px; font-weight: 700; }
        .badge.bad { background: #d2333322; color: #d23; }
        .detail { padding: 0 12px 12px; }
        .reasons { margin: 4px 0; } .reasons li { color: #d23; }
        .errline { margin: 4px 0; }
        .flagdetail { color: #888; font-size: 12px; }
        table.diff { width: 100%; border-collapse: collapse; font: 12px/1.4 ui-monospace, monospace; margin-top: 8px; }
        table.diff td { vertical-align: top; padding: 0 6px; white-space: pre-wrap; word-break: break-all; }
        table.diff td.ln { text-align: right; color: #888; word-break: keep-all; user-select: none; border-right: 1px solid #8884; }
        table.diff td.tx { width: 520px; }
        td.tx.ins, td.ln.ins { background: #1a99331f; }
        td.tx.del, td.ln.del { background: #d2333322; }
        td.tx.mod, td.ln.mod { background: #e8800022; }
        td.imag { background: #8881; }
        tr.skip td { text-align: center; color: #888; background: #8881; font-style: italic; padding: 2px; }
        table.list { width: 100%; border-collapse: collapse; }
        table.list th { text-align: left; color: #888; font-weight: 600; border-bottom: 1px solid #8884; padding: 4px 8px; }
        table.list td { padding: 3px 8px; border-bottom: 1px solid #8882; font-family: ui-monospace, monospace; word-break: break-all; }
        a { color: #28d; }
        """;
}
