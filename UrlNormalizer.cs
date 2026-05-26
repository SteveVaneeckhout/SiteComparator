namespace SiteComparator;

/// <summary>
/// Turns absolute URLs into stable, host-relative "page keys" so the same logical page
/// can be matched between two sites living on different domains, and decides which links
/// are worth crawling.
/// </summary>
public static class UrlNormalizer
{
    // Resources that are not "pages" — never enqueue these for crawling.
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".map", ".json", ".xml", ".rss", ".txt",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".pdf", ".zip", ".gz", ".rar", ".7z", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp4", ".webm", ".mp3", ".wav", ".avi", ".mov", ".m4v",
    };

    public static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    public static bool IsInternal(Uri uri, Uri baseUrl) =>
        IsHttp(uri) && string.Equals(uri.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase);

    public static bool IsLikelyAsset(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        return ext.Length > 0 && AssetExtensions.Contains(ext);
    }

    /// <summary>
    /// Resolve a discovered href against the page it was found on. Returns false for
    /// fragments, mailto:/tel:/javascript:/data: and anything that isn't http(s).
    /// </summary>
    public static bool TryResolve(Uri pageUrl, string? href, out Uri resolved)
    {
        resolved = pageUrl;
        if (string.IsNullOrWhiteSpace(href)) return false;

        href = href.Trim();
        if (href.Length == 0 || href[0] == '#') return false;

        foreach (var scheme in NonNavigationalSchemes)
        {
            if (href.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return Uri.TryCreate(pageUrl, href, out resolved!) && IsHttp(resolved);
    }

    private static readonly string[] NonNavigationalSchemes =
        { "javascript:", "mailto:", "tel:", "data:", "blob:", "ftp:", "file:" };

    // Default documents that a server serves for a directory, so "/about/index.html"
    // and "/about/" (and "/" vs "/index.html") match as one page.
    private static readonly HashSet<string> DefaultDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.html", "index.htm", "index.php", "index.asp", "index.aspx",
        "default.htm", "default.html", "default.asp", "default.aspx",
        "home.html", "home.htm",
    };

    /// <summary>
    /// Build the cross-site matching key: scheme and host are dropped, the path is
    /// lower-cased with a normalized trailing slash, the fragment is removed, and query
    /// parameters are sorted (or dropped) for stable comparison.
    /// </summary>
    public static string ToKey(Uri uri, bool stripQuery)
    {
        var path = uri.AbsolutePath;

        // Drop a trailing default document, leaving the directory (e.g. "/a/index.html" -> "/a/").
        var lastSlash = path.LastIndexOf('/');
        if (lastSlash >= 0 && DefaultDocuments.Contains(path[(lastSlash + 1)..]))
        {
            path = path[..(lastSlash + 1)];
        }

        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }
        if (path.Length == 0)
        {
            path = "/";
        }
        path = path.ToLowerInvariant();

        if (stripQuery || string.IsNullOrEmpty(uri.Query))
        {
            return path;
        }

        var parts = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(parts, StringComparer.Ordinal);
        return $"{path}?{string.Join('&', parts).ToLowerInvariant()}";
    }
}
