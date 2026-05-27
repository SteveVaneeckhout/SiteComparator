# SiteComparator

A .NET console tool that crawls **two versions of the same website** and compares their
HTML to catch *subtle, invisible breakage* — parts of a page silently dropping out, or
server-side errors and exceptions leaking into the markup that don't show up when you just
click around in a browser.

It was built for **CMS upgrades**, where:

- the new version may render pages slightly (or badly) differently, and
- the old instance's database is often weeks or months out of date, so the two sites no
  longer have exactly the same set of pages.

SiteComparator handles both: it matches pages that exist on **both** sites and diffs their
HTML, while separately listing the pages that exist on only one side.

## How it works

1. **Crawl** each site breadth-first from its base URL, following internal `<a href>` links
   (assets like images/CSS/JS are skipped), up to the configured page/depth limits.
2. **Match** pages across the two different domains using a *host-relative key* (e.g.
   `https://old.example.com/about/` and `https://new.example.com/about/index.html` both map
   to `/about`). Default documents (`index.html`, `default.aspx`, …) and trailing slashes are
   normalized so equivalent URLs line up.
3. **Normalize** each page's HTML before diffing, so per-request noise doesn't masquerade as
   breakage. The normalizer:
   - drops `<script>`, `<style>`, `<noscript>`, `<template>` and HTML comments,
   - blanks framework anti-forgery / viewstate values (`__VIEWSTATE`, `__EVENTVALIDATION`,
     `__RequestVerificationToken`, CSRF tokens, …),
   - removes `nonce` / `integrity` / CSP-style attributes,
   - strips cache-busting query strings from asset URLs (`style.css?v=123`),
   - rewrites links — and self-referential meta URLs (`og:url`, `og:image`, `twitter:*`, …) —
     that point at the page's **own host** to host-neutral paths (so old-domain vs new-domain
     URLs aren't reported as differences),
   - collapses insignificant whitespace and sorts attributes.
4. **Compare** the normalized HTML and produce a similarity score (0–100%), a visible-text
   similarity score, and a side-by-side diff for anything that differs.
5. **Scan every page** (on both sites, independently) for trouble a casual browse misses:
   ASP.NET / runtime error pages, leaked stack traces and `System.*Exception` text,
   un-rendered template syntax (`@Model`, `<%= %>`, …), and bad HTTP statuses — including the
   nasty case of a `200 OK` page whose body actually contains an error.
6. **Report** everything as a self-contained HTML file plus a console summary.

## Requirements

- [.NET SDK 10.0+](https://dotnet.microsoft.com/download)

Dependencies (restored automatically): [AngleSharp](https://anglesharp.github.io/) for HTML
parsing/normalization and [DiffPlex](https://github.com/mmanela/diffplex) for diffs.

## Build

```sh
dotnet build
```

## Usage

```sh
dotnet run -- <oldBaseUrl> <newBaseUrl> [options]
```

Example:

```sh
dotnet run -- https://old.example.com https://new.example.com --max-pages 200
```

Then open the generated `site-comparison-report.html`.

### Options

| Option | Default | Description |
|---|---|---|
| `--max-pages <n>` | `500` | Max pages to crawl per site |
| `--max-depth <n>` | `20` | Max link depth from the base URL |
| `--concurrency <n>` | `4` | Parallel requests per site |
| `--delay-ms <n>` | `0` | Delay after each request (be polite to fragile servers) |
| `--timeout <seconds>` | `30` | Per-request timeout |
| `--threshold <pct>` | `98` | Similarity % below which a page is flagged as a regression |
| `--strip-query` | off | Ignore query strings when matching pages (treats `/p?id=1` and `/p?id=2` as one page) |
| `--include <regex>` | — | Only crawl URLs whose absolute string matches this regex (case-insensitive) |
| `--exclude <regex>` | — | Skip URLs matching this regex (case-insensitive) |
| `--output <path>` | `site-comparison-report.html` | HTML report path |
| `-h`, `--help` | — | Show help |

## Output

**HTML report** — summary cards, then:

- **Differences & regressions** — every shared page that isn't identical, worst first, each
  expandable to a side-by-side diff (unchanged regions collapsed). Regressions are badged.
- **Only on OLD site** — pages the new site no longer serves (removed, or just the stale-DB gap).
- **Only on NEW site** — pages added since the old database snapshot.
- **Flagged errors (NEW / OLD)** — pages where the error scanner found something.
- **Identical pages** — collapsed list.

**Console summary** — counts plus the top regressions with reasons.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | No regressions |
| `1` | One or more regressions found (below-threshold similarity, a new-side error, or a status regression) |
| `2` | Invalid arguments |

This makes it usable as a gate in CI.

## Tuning

- **Threshold too noisy / too quiet?** Adjust `--threshold` (e.g. `95` is looser).
- **A recurring, harmless diff** (e.g. a per-request build hash or timestamp that isn't covered)
  shows up on many pages? Add the pattern to the volatile rules near the top of
  `HtmlNormalizer.cs` — they're all in one place — and re-run.
- **Crawler wandering into the wrong areas** (logout links, print views, faceted search with
  endless query combinations)? Use `--exclude`, `--include`, or `--strip-query`.

## Notes

- Assumes **server-rendered HTML** (the markup arrives complete in the HTTP response). It does
  not execute JavaScript, so client-side-rendered SPAs would need a headless browser instead.
- Assumes the sites are reachable **without authentication**.
- `robots.txt` is **not** consulted — this is intended for QA against your own sites.
