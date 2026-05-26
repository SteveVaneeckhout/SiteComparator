using System.Text.RegularExpressions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace SiteComparator;

public enum Verdict
{
    Identical,
    NearIdentical,
    Different,
}

/// <summary>The result of comparing one page that exists on both sites.</summary>
public sealed record PageComparison
{
    public required string Key { get; init; }
    public required string OldUrl { get; init; }
    public required string NewUrl { get; init; }
    public int OldStatus { get; init; }
    public int NewStatus { get; init; }

    /// <summary>Normalized-HTML line similarity, 0..100. The primary "sameness" score.</summary>
    public double Similarity { get; init; }

    /// <summary>Visible-text bag-of-words similarity, 0..100. Good "did content go missing" signal.</summary>
    public double TextSimilarity { get; init; }

    public Verdict Verdict { get; init; }
    public bool IsRegression { get; init; }
    public IReadOnlyList<string> RegressionReasons { get; init; } = [];

    public IReadOnlyList<ErrorFlag> OldErrors { get; init; } = [];
    public IReadOnlyList<ErrorFlag> NewErrors { get; init; } = [];

    /// <summary>Side-by-side diff of normalized HTML; populated only when the pages differ.</summary>
    public SideBySideDiffModel? Diff { get; init; }
}

/// <summary>
/// Compares a single page across the two sites: normalizes both, scores their HTML and
/// text similarity, classifies the verdict, and decides whether it's a regression.
/// </summary>
public static class PageComparer
{
    private static readonly IDiffer Differ = new Differ();
    private static readonly InlineDiffBuilder InlineBuilder = new(Differ);
    private static readonly SideBySideDiffBuilder SideBySideBuilder = new(Differ);
    private static readonly Regex WordToken = new(@"[\w]+", RegexOptions.Compiled);

    public static PageComparison Compare(PageResult oldPage, PageResult newPage, Options options)
    {
        var oldNorm = HtmlNormalizer.Normalize(oldPage.RawHtml, options.OldBaseUrl);
        var newNorm = HtmlNormalizer.Normalize(newPage.RawHtml, options.NewBaseUrl);

        var similarity = LineSimilarity(oldNorm.Html, newNorm.Html);
        var textSimilarity = WordOverlap(oldNorm.VisibleText, newNorm.VisibleText);

        var verdict = similarity >= 99.999 ? Verdict.Identical
            : similarity >= options.Threshold ? Verdict.NearIdentical
            : Verdict.Different;

        var oldErrors = ErrorScanner.Scan(oldPage);
        var newErrors = ErrorScanner.Scan(newPage);

        var reasons = new List<string>();
        if (verdict == Verdict.Different)
        {
            reasons.Add($"HTML similarity {similarity:0.0}% is below the {options.Threshold:0.#}% threshold");
        }

        var oldCategories = oldErrors.Select(e => e.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newOnlyErrors = newErrors.Where(e => !oldCategories.Contains(e.Category)).ToList();
        if (newOnlyErrors.Count > 0)
        {
            reasons.Add("New-site errors not present on old: " +
                string.Join(", ", newOnlyErrors.Select(e => e.Category).Distinct()));
        }

        if (newPage.StatusCode >= 400 && oldPage.StatusCode is > 0 and < 400)
        {
            reasons.Add($"Status regressed {oldPage.StatusCode} -> {newPage.StatusCode}");
        }

        if (!newPage.Fetched && oldPage.Fetched)
        {
            reasons.Add($"New site failed to fetch: {newPage.FetchError}");
        }

        return new PageComparison
        {
            Key = oldPage.Key,
            OldUrl = oldPage.AbsoluteUrl,
            NewUrl = newPage.AbsoluteUrl,
            OldStatus = oldPage.StatusCode,
            NewStatus = newPage.StatusCode,
            Similarity = similarity,
            TextSimilarity = textSimilarity,
            Verdict = verdict,
            IsRegression = reasons.Count > 0,
            RegressionReasons = reasons,
            OldErrors = oldErrors,
            NewErrors = newErrors,
            Diff = verdict == Verdict.Identical
                ? null
                : SideBySideBuilder.BuildDiffModel(oldNorm.Html, newNorm.Html),
        };
    }

    /// <summary>Fraction of normalized lines that are unchanged, as a percentage.</summary>
    private static double LineSimilarity(string oldHtml, string newHtml)
    {
        if (oldHtml == newHtml) return 100.0;

        var model = InlineBuilder.BuildDiffModel(oldHtml, newHtml);
        var unchanged = 0;
        var changed = 0;
        foreach (var line in model.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Unchanged:
                    unchanged++;
                    break;
                case ChangeType.Imaginary:
                    break;
                default:
                    changed++;
                    break;
            }
        }

        var total = unchanged + changed;
        return total == 0 ? 100.0 : 100.0 * unchanged / total;
    }

    /// <summary>Multiset (bag-of-words) overlap of visible text, as a percentage.</summary>
    private static double WordOverlap(string oldText, string newText)
    {
        var a = Tokenize(oldText);
        var b = Tokenize(newText);
        if (a.Count == 0 && b.Count == 0) return 100.0;

        var counts = new Dictionary<string, int>();
        foreach (var w in a)
        {
            counts[w] = counts.GetValueOrDefault(w) + 1;
        }

        var intersection = 0;
        foreach (var w in b)
        {
            if (counts.TryGetValue(w, out var c) && c > 0)
            {
                counts[w] = c - 1;
                intersection++;
            }
        }

        var union = a.Count + b.Count - intersection;
        return union == 0 ? 100.0 : 100.0 * intersection / union;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in WordToken.Matches(text.ToLowerInvariant()))
        {
            tokens.Add(m.Value);
        }
        return tokens;
    }
}
