using System.Text.RegularExpressions;

namespace SiteComparator;

/// <summary>A single problem detected on a page.</summary>
public sealed record ErrorFlag(string Category, string Detail);

/// <summary>
/// Hunts for trouble that a casual browse would miss: server-error pages that still
/// return readable markup, exception text baked into otherwise-fine pages, un-rendered
/// template syntax, and bad HTTP statuses. Runs on every fetched page on both sites,
/// so it also catches errors on pages that exist on only one side.
/// </summary>
public static class ErrorScanner
{
    // Literal substrings that strongly indicate a server/runtime error in the body.
    private static readonly (string Category, string Needle)[] BodySignatures =
    {
        ("ASP.NET error page", "Server Error in '/' Application"),
        ("ASP.NET error page", "Runtime Error"),
        ("Stack trace", "Stack Trace:"),
        ("Stack trace", "at System."),
        ("Unhandled exception", "An unhandled exception"),
        ("Unhandled exception", "An error occurred while processing your request"),
        ("Null reference", "Object reference not set to an instance"),
        ("Parser error", "Parser Error Message"),
        ("Compilation error", "Compilation Error"),
        ("Config error", "Configuration Error"),
        ("Database error", "SqlException"),
        ("Database error", "ORA-0"),
        ("PHP error", "Fatal error:"),
        ("PHP error", "Warning: "),
        ("Generic error", "Whoops, something went wrong"),
    };

    // .NET exception type names leaking into the body, e.g. System.NullReferenceException.
    private static readonly Regex ExceptionType =
        new(@"\bSystem\.[A-Za-z0-9_.]*Exception\b", RegexOptions.Compiled);

    // Template/markup that should have been rendered away before reaching the browser.
    private static readonly (string Category, string Needle)[] TemplateLeaks =
    {
        ("Razor leak", "@Model"),
        ("Razor leak", "@Html."),
        ("Razor leak", "@Url."),
        ("Razor leak", "@ViewBag"),
        ("Razor leak", "@RenderBody"),
        ("WebForms leak", "<%="),
        ("WebForms leak", "<%#"),
        ("WebForms leak", "<%:"),
        ("WebForms leak", "<%$"),
    };

    public static IReadOnlyList<ErrorFlag> Scan(PageResult page)
    {
        var flags = new List<ErrorFlag>();

        if (!page.Fetched)
        {
            flags.Add(new ErrorFlag("Fetch failed", page.FetchError ?? "unknown error"));
            return flags;
        }

        if (page.StatusCode >= 400)
        {
            flags.Add(new ErrorFlag("HTTP error", $"Status {page.StatusCode}"));
        }

        var html = page.RawHtml;
        if (string.IsNullOrEmpty(html))
        {
            return flags;
        }

        foreach (var (category, needle) in BodySignatures)
        {
            if (html.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(new ErrorFlag(category, Quote(needle)));
            }
        }

        var match = ExceptionType.Match(html);
        if (match.Success)
        {
            flags.Add(new ErrorFlag(".NET exception", match.Value));
        }

        foreach (var (category, needle) in TemplateLeaks)
        {
            if (html.Contains(needle, StringComparison.Ordinal))
            {
                flags.Add(new ErrorFlag(category, Quote(needle)));
            }
        }

        return flags;
    }

    private static string Quote(string needle) => $"found \"{needle.Trim()}\"";
}
