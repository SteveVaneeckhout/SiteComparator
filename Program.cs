using System.Net;
using SiteComparator;

if (Options.WantsHelp(args))
{
    Console.WriteLine(Options.Usage);
    return 0;
}

if (!Options.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine($"Error: {error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 10,
    AutomaticDecompression = DecompressionMethods.All,
};
using var http = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(options!.TimeoutSeconds),
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("SiteComparator/1.0 (+CMS-upgrade-QA)");
http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");

var comparison = new SiteComparison(http, options);
var result = await comparison.RunAsync(Console.WriteLine);

ReportGenerator.WriteConsoleSummary(result);
var reportPath = ReportGenerator.WriteHtmlReport(result, options.OutputPath);
Console.WriteLine($"HTML report written to: {reportPath}");

// Non-zero exit when regressions were found, so the tool is CI-friendly.
return result.RegressionCount > 0 ? 1 : 0;
