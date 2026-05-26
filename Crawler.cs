using System.Collections.Concurrent;
using AngleSharp.Html.Parser;

namespace SiteComparator;

/// <summary>
/// Breadth-first crawler for a single site. Fetches pages with a shared HttpClient,
/// extracts internal links with AngleSharp, and stops at the configured page/depth limits.
/// </summary>
public sealed class Crawler
{
    private readonly HttpClient _http;
    private readonly Options _options;
    private readonly HtmlParser _parser = new();

    public Crawler(HttpClient http, Options options)
    {
        _http = http;
        _options = options;
    }

    public async Task<Dictionary<string, PageResult>> CrawlAsync(
        Uri baseUrl, Action<string>? log = null, CancellationToken ct = default)
    {
        var results = new ConcurrentDictionary<string, PageResult>();
        var seen = new ConcurrentDictionary<string, byte>();

        var startKey = UrlNormalizer.ToKey(baseUrl, _options.StripQuery);
        seen[startKey] = 0;
        var current = new List<Uri> { baseUrl };

        for (var depth = 0; depth <= _options.MaxDepth && current.Count > 0; depth++)
        {
            if (results.Count >= _options.MaxPages) break;

            var discovered = new ConcurrentDictionary<string, Uri>();
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.Concurrency,
                CancellationToken = ct,
            };

            await Parallel.ForEachAsync(current, parallel, async (url, token) =>
            {
                if (results.Count >= _options.MaxPages) return;

                var page = await FetchAsync(url, token);
                results[page.Key] = page;
                log?.Invoke($"  [{results.Count,4}] {page.StatusCode,3} {page.Key}");

                if (_options.DelayMs > 0)
                {
                    await Task.Delay(_options.DelayMs, token);
                }

                if (depth >= _options.MaxDepth || !page.Fetched || !page.IsHtml || string.IsNullOrEmpty(page.RawHtml))
                {
                    return;
                }

                foreach (var link in ExtractLinks(page.RawHtml, url, baseUrl))
                {
                    var key = UrlNormalizer.ToKey(link, _options.StripQuery);
                    if (seen.TryAdd(key, 0))
                    {
                        discovered[key] = link;
                    }
                }
            });

            current = discovered.Values.ToList();
        }

        return new Dictionary<string, PageResult>(results);
    }

    private async Task<PageResult> FetchAsync(Uri url, CancellationToken ct)
    {
        var key = UrlNormalizer.ToKey(url, _options.StripQuery);
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            var isHtml = contentType is null
                || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase);

            var body = isHtml ? await resp.Content.ReadAsStringAsync(ct) : "";

            return new PageResult
            {
                Key = key,
                AbsoluteUrl = url.AbsoluteUri,
                StatusCode = (int)resp.StatusCode,
                ContentType = contentType,
                RawHtml = body,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PageResult
            {
                Key = key,
                AbsoluteUrl = url.AbsoluteUri,
                StatusCode = 0,
                FetchError = ex.Message,
            };
        }
    }

    private IEnumerable<Uri> ExtractLinks(string html, Uri pageUrl, Uri baseUrl)
    {
        var doc = _parser.ParseDocument(html);
        var found = new List<Uri>();

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (!UrlNormalizer.TryResolve(pageUrl, href, out var link)) continue;
            if (!UrlNormalizer.IsInternal(link, baseUrl)) continue;
            if (UrlNormalizer.IsLikelyAsset(link)) continue;

            var abs = link.AbsoluteUri;
            if (_options.Include is { } inc && !inc.IsMatch(abs)) continue;
            if (_options.Exclude is { } exc && exc.IsMatch(abs)) continue;

            found.Add(link);
        }

        return found;
    }
}
