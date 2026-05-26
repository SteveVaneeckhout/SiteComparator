namespace SiteComparator;

/// <summary>A page that triggered the error scanner, with its flags.</summary>
public sealed record FlaggedPage(PageResult Page, IReadOnlyList<ErrorFlag> Flags);

/// <summary>The full outcome of comparing the two sites.</summary>
public sealed record ComparisonResult
{
    public required Options Options { get; init; }
    public required IReadOnlyList<PageComparison> Compared { get; init; }
    public required IReadOnlyList<PageResult> OnlyOld { get; init; }
    public required IReadOnlyList<PageResult> OnlyNew { get; init; }
    public required IReadOnlyList<FlaggedPage> OldErrorPages { get; init; }
    public required IReadOnlyList<FlaggedPage> NewErrorPages { get; init; }
    public int OldPageCount { get; init; }
    public int NewPageCount { get; init; }

    public int RegressionCount => Compared.Count(c => c.IsRegression);
    public int IdenticalCount => Compared.Count(c => c.Verdict == Verdict.Identical);
    public int NearIdenticalCount => Compared.Count(c => c.Verdict == Verdict.NearIdentical);
    public int DifferentCount => Compared.Count(c => c.Verdict == Verdict.Different);
}

/// <summary>
/// Orchestrates the whole run: crawl both sites, match pages by key, compare the shared
/// ones, bucket the rest, and scan every page for errors.
/// </summary>
public sealed class SiteComparison
{
    private readonly HttpClient _http;
    private readonly Options _options;

    public SiteComparison(HttpClient http, Options options)
    {
        _http = http;
        _options = options;
    }

    public async Task<ComparisonResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var crawler = new Crawler(_http, _options);

        log?.Invoke($"Crawling OLD site: {_options.OldBaseUrl}");
        log?.Invoke($"Crawling NEW site: {_options.NewBaseUrl}");

        var oldTask = crawler.CrawlAsync(_options.OldBaseUrl, m => log?.Invoke("[OLD]" + m), ct);
        var newTask = crawler.CrawlAsync(_options.NewBaseUrl, m => log?.Invoke("[NEW]" + m), ct);
        await Task.WhenAll(oldTask, newTask);

        var oldMap = oldTask.Result;
        var newMap = newTask.Result;

        log?.Invoke($"Crawled {oldMap.Count} old pages, {newMap.Count} new pages. Comparing...");

        var sharedKeys = oldMap.Keys.Intersect(newMap.Keys).ToList();
        var compared = sharedKeys
            .Select(k => PageComparer.Compare(oldMap[k], newMap[k], _options))
            // Worst first: regressions, then ascending similarity.
            .OrderByDescending(c => c.IsRegression)
            .ThenBy(c => c.Similarity)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        var onlyOld = oldMap.Keys.Except(newMap.Keys)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => oldMap[k])
            .ToList();

        var onlyNew = newMap.Keys.Except(oldMap.Keys)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => newMap[k])
            .ToList();

        return new ComparisonResult
        {
            Options = _options,
            Compared = compared,
            OnlyOld = onlyOld,
            OnlyNew = onlyNew,
            OldErrorPages = ScanForErrors(oldMap),
            NewErrorPages = ScanForErrors(newMap),
            OldPageCount = oldMap.Count,
            NewPageCount = newMap.Count,
        };
    }

    private static List<FlaggedPage> ScanForErrors(Dictionary<string, PageResult> pages)
    {
        return pages.Values
            .Select(p => new FlaggedPage(p, ErrorScanner.Scan(p)))
            .Where(f => f.Flags.Count > 0)
            .OrderBy(f => f.Page.Key, StringComparer.Ordinal)
            .ToList();
    }
}
