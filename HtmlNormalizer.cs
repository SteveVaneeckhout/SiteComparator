using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;

namespace SiteComparator;

/// <summary>The product of normalizing a page: structure-only HTML plus its visible text.</summary>
public sealed record NormalizedHtml(string Html, string VisibleText);

/// <summary>
/// Reduces raw HTML to a canonical form so that comparing two pages surfaces *meaningful*
/// differences and ignores per-request noise. This is the heart of "sameness":
///
///  - drops scripts, styles, noscript and comments (volatile, not visible content),
///  - blanks framework anti-forgery / viewstate / nonce / csrf values,
///  - strips cache-busting query strings from asset URLs,
///  - rewrites links (and self-referential meta URLs such as og:url) that point at the
///    page's own host to host-neutral paths (so old-domain vs new-domain links don't read
///    as differences),
///  - strips the page's own origin out of links that *embed* it — e.g. social share buttons
///    like facebook.com/sharer?u=&lt;own-url&gt; — leaving only the shared path,
///  - collapses insignificant whitespace, and
///  - sorts attributes for a stable serialization.
///
/// Both sides of a comparison run through the identical pipeline, so the formatter's own
/// layout choices cancel out and never produce false diffs.
/// </summary>
public static class HtmlNormalizer
{
    private static readonly HtmlParser Parser = new();

    private static readonly PrettyMarkupFormatter Formatter = new()
    {
        Indentation = "  ",
        NewLine = "\n",
    };

    // Removed wholesale — these elements are noise for a visible-structure comparison.
    private const string NoiseSelector = "script, style, noscript, template";

    // <input>/<meta> whose value changes every request and means nothing for sameness.
    private static readonly HashSet<string> VolatileFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__VIEWSTATE", "__VIEWSTATEGENERATOR", "__EVENTVALIDATION", "__EVENTTARGET",
        "__EVENTARGUMENT", "__PREVIOUSPAGE", "__VIEWSTATEENCRYPTED",
        "__RequestVerificationToken", "RequestVerificationToken",
        "csrf-token", "csrf_token", "csrfmiddlewaretoken", "authenticity_token",
        "_csrf", "_token", "xsrf-token",
    };

    // Attributes blanked/removed on every element regardless of tag.
    private static readonly HashSet<string> VolatileAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nonce", "data-nonce", "integrity",
    };

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // Asset-bearing elements whose URL query string is a cache-buster we should drop.
    private static readonly HashSet<string> AssetTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "link", "script", "img", "source", "track", "embed", "audio", "video",
    };

    // Cache, keyed by host, of a regex that strips that origin (scheme://host[:port]) wherever it
    // appears *inside* a value — including the percent-encoded form share links use. Built lazily
    // because the host isn't known until a page's base URL is in hand.
    private static readonly ConcurrentDictionary<string, Regex> EmbeddedOriginPatterns = new();

    public static NormalizedHtml Normalize(string rawHtml, Uri baseUrl)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return new NormalizedHtml("", "");
        }

        var doc = Parser.ParseDocument(rawHtml);

        RemoveNoiseElements(doc);
        RemoveComments(doc);
        NormalizeElements(doc, baseUrl);
        CollapseWhitespace(doc);

        var root = doc.DocumentElement;
        var html = root is null ? "" : root.ToHtml(Formatter);

        var bodyText = doc.Body?.TextContent ?? doc.DocumentElement?.TextContent ?? "";
        var visibleText = WhitespaceRun.Replace(bodyText, " ").Trim();

        return new NormalizedHtml(html, visibleText);
    }

    private static void RemoveNoiseElements(IDocument doc)
    {
        foreach (var el in doc.QuerySelectorAll(NoiseSelector).ToList())
        {
            el.Parent?.RemoveChild(el);
        }
    }

    private static void RemoveComments(IDocument doc)
    {
        foreach (var comment in doc.Descendants<IComment>().ToList())
        {
            comment.Parent?.RemoveChild(comment);
        }
    }

    private static void NormalizeElements(IDocument doc, Uri baseUrl)
    {
        foreach (var el in doc.All.ToList())
        {
            BlankVolatileFieldValue(el);
            RemoveVolatileAttributes(el);
            NormalizeUrlAttributes(el, baseUrl);
            SortAttributes(el);
        }
    }

    private static void BlankVolatileFieldValue(IElement el)
    {
        var name = el.GetAttribute("name");
        if ((el.LocalName == "input" || el.LocalName == "meta") &&
            name is not null && VolatileFieldNames.Contains(name))
        {
            if (el.HasAttribute("value")) el.SetAttribute("value", "");
            if (el.HasAttribute("content")) el.SetAttribute("content", "");
        }
    }

    private static void RemoveVolatileAttributes(IElement el)
    {
        foreach (var attr in el.Attributes.Select(a => a.Name).ToList())
        {
            if (VolatileAttributes.Contains(attr) || attr.Contains("csrf", StringComparison.OrdinalIgnoreCase))
            {
                el.RemoveAttribute(attr);
            }
        }
    }

    private static void NormalizeUrlAttributes(IElement el, Uri baseUrl)
    {
        var isAsset = AssetTags.Contains(el.LocalName);

        foreach (var attr in new[] { "href", "src", "action" })
        {
            var value = el.GetAttribute(attr);
            if (value is null) continue;
            value = NormalizeUrlValue(value, baseUrl, stripQuery: isAsset);
            el.SetAttribute(attr, NeutralizeEmbeddedOrigins(value, baseUrl));
        }

        // <meta property="og:url" content="https://own-host/path"> (and og:image, twitter:*, …)
        // embed an absolute, self-referential URL in `content`. Host-neutralize it like any link
        // so the source domain isn't read as a difference. NormalizeUrlValue only rewrites
        // own-host absolute URLs, so non-URL content (descriptions, viewport, …) passes through.
        if (el.LocalName == "meta")
        {
            var content = el.GetAttribute("content");
            if (content is not null)
            {
                el.SetAttribute("content", NormalizeUrlValue(content, baseUrl, stripQuery: false));
            }
        }

        // srcset is a comma-separated list of "url descriptor" candidates.
        var srcset = el.GetAttribute("srcset");
        if (srcset is not null)
        {
            var rewritten = srcset
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(candidate =>
                {
                    var bits = candidate.Split(' ', 2, StringSplitOptions.TrimEntries);
                    bits[0] = NormalizeUrlValue(bits[0], baseUrl, stripQuery: true);
                    return string.Join(' ', bits);
                });
            el.SetAttribute("srcset", string.Join(", ", rewritten));
        }
    }

    private static string NormalizeUrlValue(string value, Uri baseUrl, bool stripQuery)
    {
        var v = value.Trim();
        if (v.Length == 0) return v;

        // Protocol-relative URL ("//host/path") -> give it a scheme so it parses.
        if (v.StartsWith("//", StringComparison.Ordinal))
        {
            v = baseUrl.Scheme + ":" + v;
        }

        if (Uri.TryCreate(v, UriKind.Absolute, out var abs) && UrlNormalizer.IsHttp(abs))
        {
            if (string.Equals(abs.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase))
            {
                // Own-host link -> collapse to a host-neutral path so the two domains line up.
                var path = abs.AbsolutePath;
                if (path.Length == 0) path = "/";
                return stripQuery ? path : path + abs.Query;
            }

            // External link: leave it, but assets still get their cache-buster removed.
            return stripQuery ? abs.GetLeftPart(UriPartial.Path) : v;
        }

        // Relative URL: only thing to canonicalize is the asset cache-buster.
        if (stripQuery)
        {
            var q = v.IndexOf('?');
            if (q >= 0) v = v[..q];
        }
        return v;
    }

    /// <summary>
    /// Strip the page's own origin out of any URL that *embeds* it. Social share buttons carry the
    /// current page's absolute URL inside a query parameter or text, usually percent-encoded — e.g.
    /// <c>facebook.com/sharer/sharer.php?u=https%3a%2f%2fown-host%2fpage</c>. That embedded origin
    /// carries the source domain, so two environments differ there even when the shared page is the
    /// same; dropping it (and keeping the path) lines them up while still surfacing real path diffs.
    /// The host appears verbatim even when the surrounding "://" is encoded, so a cheap Contains
    /// check skips the regex for the vast majority of links.
    /// </summary>
    private static string NeutralizeEmbeddedOrigins(string value, Uri baseUrl)
    {
        if (value.Length == 0 || !value.Contains(baseUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var pattern = EmbeddedOriginPatterns.GetOrAdd(baseUrl.Host, host =>
            new Regex(
                $@"https?(?:://|%3a%2f%2f){Regex.Escape(host)}(?:(?::|%3a)\d+)?(?=/|%2f|\?|%3f|#|%23|&|%26|[""']|$)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled));

        return pattern.Replace(value, string.Empty);
    }

    private static void SortAttributes(IElement el)
    {
        var attrs = el.Attributes.Select(a => (a.Name, a.Value)).ToList();
        if (attrs.Count < 2) return;

        var sorted = attrs.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        if (attrs.SequenceEqual(sorted)) return;

        foreach (var (name, _) in attrs) el.RemoveAttribute(name);
        foreach (var (name, value) in sorted) el.SetAttribute(name, value);
    }

    private static void CollapseWhitespace(IDocument doc)
    {
        foreach (var text in doc.Descendants<IText>().ToList())
        {
            if (IsPreformatted(text)) continue;
            text.Data = WhitespaceRun.Replace(text.Data, " ");
        }
    }

    private static bool IsPreformatted(INode node)
    {
        for (var p = node.ParentElement; p is not null; p = p.ParentElement)
        {
            if (p.LocalName is "pre" or "textarea") return true;
        }
        return false;
    }
}
