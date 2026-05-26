using System.Text.RegularExpressions;

namespace SiteComparator;

/// <summary>
/// Parsed command-line configuration. All knobs have sensible defaults; only the two
/// base URLs are required.
/// </summary>
public sealed class Options
{
    public required Uri OldBaseUrl { get; init; }
    public required Uri NewBaseUrl { get; init; }

    public int MaxPages { get; init; } = 500;
    public int MaxDepth { get; init; } = 20;
    public int Concurrency { get; init; } = 4;
    public int DelayMs { get; init; }
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Pages at or above this normalized-HTML similarity (%) are not treated as regressions.</summary>
    public double Threshold { get; init; } = 98.0;

    /// <summary>Drop query strings when building the page key (treats "/p?id=1" and "/p?id=2" as one page).</summary>
    public bool StripQuery { get; init; }

    /// <summary>Only crawl URLs whose absolute string matches this pattern.</summary>
    public Regex? Include { get; init; }

    /// <summary>Never crawl URLs whose absolute string matches this pattern.</summary>
    public Regex? Exclude { get; init; }

    public string OutputPath { get; init; } = "site-comparison-report.html";

    public const string Usage = """
        SiteComparator — crawl two versions of a site and compare their HTML.

        Usage:
          SiteComparator <oldBaseUrl> <newBaseUrl> [options]

        Arguments:
          oldBaseUrl            Base URL of the OLD/reference site (e.g. https://old.example.com)
          newBaseUrl            Base URL of the NEW/upgraded site (e.g. https://new.example.com)

        Options:
          --max-pages <n>       Max pages to crawl per site            (default 500)
          --max-depth <n>       Max link depth from the base URL       (default 20)
          --concurrency <n>     Parallel requests per site             (default 4)
          --delay-ms <n>        Delay after each request, milliseconds  (default 0)
          --timeout <seconds>   Per-request timeout                    (default 30)
          --threshold <pct>     Similarity % below which a page is a regression (default 98)
          --strip-query         Ignore query strings when matching pages
          --include <regex>     Only crawl URLs matching this regex (case-insensitive)
          --exclude <regex>     Skip URLs matching this regex (case-insensitive)
          --output <path>       HTML report path (default site-comparison-report.html)
          -h, --help            Show this help

        Example:
          SiteComparator https://old.example.com https://new.example.com --max-pages 200
        """;

    public static bool WantsHelp(string[] args) =>
        args.Any(a => a is "-h" or "--help" or "/?" or "/h");

    public static bool TryParse(string[] args, out Options? options, out string? error)
    {
        options = null;
        error = null;

        var positionals = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                string value;
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                else
                {
                    value = "true"; // bare boolean flag
                }

                flags[name] = value;
            }
            else
            {
                positionals.Add(a);
            }
        }

        if (positionals.Count < 2)
        {
            error = "Two base URLs are required: the old site, then the new site.";
            return false;
        }

        if (!TryUrl(positionals[0], out var oldUrl, out error)) return false;
        if (!TryUrl(positionals[1], out var newUrl, out error)) return false;

        if (!TryInt(flags, "max-pages", 500, out var maxPages, out error)) return false;
        if (!TryInt(flags, "max-depth", 20, out var maxDepth, out error)) return false;
        if (!TryInt(flags, "concurrency", 4, out var concurrency, out error)) return false;
        if (!TryInt(flags, "delay-ms", 0, out var delayMs, out error)) return false;
        if (!TryInt(flags, "timeout", 30, out var timeout, out error)) return false;
        if (!TryDouble(flags, "threshold", 98.0, out var threshold, out error)) return false;
        if (!TryRegex(flags, "include", out var include, out error)) return false;
        if (!TryRegex(flags, "exclude", out var exclude, out error)) return false;

        options = new Options
        {
            OldBaseUrl = oldUrl!,
            NewBaseUrl = newUrl!,
            MaxPages = Math.Max(1, maxPages),
            MaxDepth = Math.Max(0, maxDepth),
            Concurrency = Math.Clamp(concurrency, 1, 64),
            DelayMs = Math.Max(0, delayMs),
            TimeoutSeconds = Math.Max(1, timeout),
            Threshold = Math.Clamp(threshold, 0, 100),
            StripQuery = flags.ContainsKey("strip-query"),
            Include = include,
            Exclude = exclude,
            OutputPath = flags.TryGetValue("output", out var o) ? o : "site-comparison-report.html",
        };
        return true;
    }

    private static bool TryUrl(string raw, out Uri? url, out string? error)
    {
        error = null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out url) &&
            (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        url = null;
        error = $"Invalid URL (must be http/https): {raw}";
        return false;
    }

    private static bool TryInt(Dictionary<string, string> flags, string name, int def, out int value, out string? error)
    {
        error = null;
        value = def;
        if (!flags.TryGetValue(name, out var raw)) return true;
        if (int.TryParse(raw, out value)) return true;
        error = $"--{name} must be an integer (got '{raw}').";
        return false;
    }

    private static bool TryDouble(Dictionary<string, string> flags, string name, double def, out double value, out string? error)
    {
        error = null;
        value = def;
        if (!flags.TryGetValue(name, out var raw)) return true;
        if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
        error = $"--{name} must be a number (got '{raw}').";
        return false;
    }

    private static bool TryRegex(Dictionary<string, string> flags, string name, out Regex? value, out string? error)
    {
        error = null;
        value = null;
        if (!flags.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw)) return true;
        try
        {
            value = new Regex(raw, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"--{name} is not a valid regex: {ex.Message}";
            return false;
        }
    }
}
