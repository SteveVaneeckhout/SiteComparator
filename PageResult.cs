namespace SiteComparator;

/// <summary>
/// The outcome of fetching a single page from one site.
/// </summary>
public sealed record PageResult
{
    /// <summary>Host-relative identity used to match the same page across the two sites (e.g. "/about-us").</summary>
    public required string Key { get; init; }

    /// <summary>The absolute URL that was requested.</summary>
    public required string AbsoluteUrl { get; init; }

    /// <summary>HTTP status code, or 0 if the request never completed.</summary>
    public int StatusCode { get; init; }

    /// <summary>Raw response body (only populated for HTML-ish content types).</summary>
    public string RawHtml { get; init; } = "";

    /// <summary>Response Content-Type media type, if known.</summary>
    public string? ContentType { get; init; }

    /// <summary>Set when the request failed at the transport level (DNS, timeout, TLS, ...).</summary>
    public string? FetchError { get; init; }

    public bool Fetched => FetchError is null;

    public bool IsHtml =>
        ContentType is null || ContentType.Contains("html", StringComparison.OrdinalIgnoreCase);
}
