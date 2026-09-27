using ApplyEngine.Domain.Digest;
using ApplyEngine.Domain.Entities;
using ApplyEngine.Domain.Feeds;
using ApplyEngine.Domain.Processing;

// A dependency-free, runnable proof that the Phase 0 pipeline logic is
// correct: parse -> normalize -> dedupe -> filter -> digest. This is not a
// replacement for the xUnit suite in tests/ApplyEngine.Tests (add that once
// you have full NuGet access) — it's what lets the core logic be verified
// even where package restore isn't available.

int passed = 0, failed = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : $" — {detail}")}");
    }
}

Console.WriteLine("== ApplyEngine self-check ==");
Console.WriteLine();

// ---- 1. JobNormalizer -------------------------------------------------
Console.WriteLine("[JobNormalizer]");
var dirtyJob = new Job
{
    Title = "  .NET   Developer  ",
    Company = "Brightlane &amp; Co",
    Url = "https://example.com/x?utm_source=feed&id=9&utm_medium=rss",
    RawText = "<p>Great  role &amp; team</p>",
    SourceName = "test",
};
var cleanJob = JobNormalizer.Normalize(dirtyJob);
Check("collapses whitespace", cleanJob.Title == ".NET Developer", $"got '{cleanJob.Title}'");
Check("decodes HTML entities", cleanJob.Company == "Brightlane & Co", $"got '{cleanJob.Company}'");
Check("strips HTML tags from description", !cleanJob.RawText!.Contains('<'));
Check("strips utm_* tracking params but keeps real ones", cleanJob.Url == "https://example.com/x?id=9", $"got '{cleanJob.Url}'");

// Regression check for a real bug: hrefs pulled out of raw alert-email HTML
// via regex still have literal "&amp;" separators, not real "&". Splitting
// that on '&' produces "amp;trkemail=..." etc., which never matches the
// exact-name filters below — so every param after the first survived
// unstripped and the same LinkedIn posting (3-4 links differing only in
// trkEmail) never deduped. Url must be HTML-decoded before stripping.
var htmlEncodedUrlJob = new Job
{
    Title = "x",
    Company = "x",
    SourceName = "test",
    Url = "https://www.linkedin.com/comm/jobs/view/123/?trackingId=abc&amp;trk=eml-a&amp;trkEmail=eml-a",
};
var cleanedEncodedUrlJob = JobNormalizer.Normalize(htmlEncodedUrlJob);
Check("strips tracking params even when the source href used HTML-entity '&amp;' separators",
    cleanedEncodedUrlJob.Url == "https://www.linkedin.com/comm/jobs/view/123/", $"got '{cleanedEncodedUrlJob.Url}'");
Console.WriteLine();

// ---- 2. DedupeService ---------------------------------------------------
Console.WriteLine("[DedupeService]");
var jobA = new Job { Title = "Backend Dev", Company = "Acme", Url = "https://x.com/1?utm_source=a", SourceName = "s1" };
var jobB = new Job { Title = "backend dev", Company = "ACME", Url = "https://x.com/1?utm_source=b", SourceName = "s2" };
var jobC = new Job { Title = "Backend Dev", Company = "Acme", Url = "https://x.com/2", SourceName = "s1" };
Check("same job via two sources/tracking params hashes identically", DedupeService.ComputeHash(jobA) == DedupeService.ComputeHash(jobB));
Check("different job (different link) hashes differently", DedupeService.ComputeHash(jobA) != DedupeService.ComputeHash(jobC));
Console.WriteLine();

// ---- 3. KeywordFilter -----------------------------------------------------
Console.WriteLine("[KeywordFilter]");
var prefs = new Preferences
{
    MustHaveKeywords = new() { ".NET", "ASP.NET", "C#" },
    Locations = new() { "Kochi", "Trivandrum" },
    ExcludeCompanies = new() { "BlacklistedCorp" },
};
var matchJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u1", Location = "Kochi", RawText = "ASP.NET Core, SQL Server", SourceName = "s" };
var wrongStackJob = new Job { Title = "React Developer", Company = "GoodCo", Url = "u2", Location = "Kochi", RawText = "React, TypeScript only", SourceName = "s" };
var wrongLocationJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u3", Location = "Bangalore", RawText = ".NET Core", SourceName = "s" };
var remoteJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u4", Location = "Remote", RawText = ".NET Core", SourceName = "s" };
var excludedJob = new Job { Title = ".NET Developer", Company = "BlacklistedCorp", Url = "u5", Location = "Kochi", RawText = ".NET Core", SourceName = "s" };

Check("matching job passes", KeywordFilter.Evaluate(matchJob, prefs).IsMatch);
Check("wrong stack is rejected", !KeywordFilter.Evaluate(wrongStackJob, prefs).IsMatch);
Check("wrong location is rejected", !KeywordFilter.Evaluate(wrongLocationJob, prefs).IsMatch);
Check("remote always passes location filter", KeywordFilter.Evaluate(remoteJob, prefs).IsMatch);
Check("excluded company is rejected even if stack/location match", !KeywordFilter.Evaluate(excludedJob, prefs).IsMatch);

// The WeWorkRemotely bug: no structured Location, but the description text
// mentions a preferred city — this must now pass via the raw-text fallback.
var noStructuredLocationJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u6", Location = null, RawText = "ASP.NET role based in Kochi, hybrid setup", SourceName = "s" };
Check("null Location still matches via raw-text fallback", KeywordFilter.Evaluate(noStructuredLocationJob, prefs).IsMatch);

var workModePrefs = new Preferences
{
    MustHaveKeywords = new() { ".NET" },
    WorkModes = new() { "Remote", "Hybrid" },
};
var hybridJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u7", RawText = ".NET role, Hybrid, Kochi", SourceName = "s" };
var onsiteOnlyJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u8", RawText = ".NET role, five days per week in our downtown office", SourceName = "s" };
Check("Hybrid work mode passes when Hybrid is accepted", KeywordFilter.Evaluate(hybridJob, workModePrefs).IsMatch);
Check("Onsite-only job rejected when only Remote/Hybrid accepted", !KeywordFilter.Evaluate(onsiteOnlyJob, workModePrefs).IsMatch);

// A message alert has none of the keywords/location/work-mode prefs would
// look for — it must still pass, because it isn't a job posting to filter.
var messageAlert = new Job { Kind = JobKind.MessageAlert, Title = "New message on LinkedIn", Company = "LinkedIn", Url = "mailto:x#1", RawText = "no .NET or Kochi mentioned here", SourceName = "AlertMailbox" };
Check("Message alert bypasses the keyword/location/work-mode filter", KeywordFilter.Evaluate(messageAlert, workModePrefs).IsMatch);

// A closed posting must be rejected even when everything else about it is a
// perfect match — this is the whole point of the enrichment feature below.
var closedJob = new Job { Title = ".NET Developer", Company = "GoodCo", Url = "u9", Location = "Kochi", RawText = "ASP.NET Core", SourceName = "s", IsClosed = true };
Check("closed job posting is rejected even if it otherwise matches everything", !KeywordFilter.Evaluate(closedJob, prefs).IsMatch);
Console.WriteLine();

// ---- 3a. JobEnrichmentParser -----------------------------------------------
Console.WriteLine("[JobEnrichmentParser]");

var openJobHtml = """
    <html><head>
    <script type="application/ld+json">
    {"@context":"https://schema.org/","@type":"JobPosting","title":"Backend Developer","hiringOrganization":{"@type":"Organization","name":"Acme Corp"},"validThrough":"2027-01-01T00:00:00Z","datePosted":"2026-08-01"}
    </script>
    </head><body>Backend Developer at Acme Corp — apply now</body></html>
    """;
var openResult = JobEnrichmentParser.ParseHtml(openJobHtml);
Check("extracts company from JSON-LD hiringOrganization.name", openResult.Company == "Acme Corp", $"got '{openResult.Company}'");
Check("a validThrough date in the future is not treated as closed", !openResult.IsClosed);

var closedJobHtml = """
    <html><head>
    <script type="application/ld+json">
    {"@context":"https://schema.org/","@type":"JobPosting","title":"Frontend Developer","hiringOrganization":{"@type":"Organization","name":"Beta Inc"},"validThrough":"2020-01-01T00:00:00Z"}
    </script>
    </head><body>Frontend Developer at Beta Inc</body></html>
    """;
var closedResult = JobEnrichmentParser.ParseHtml(closedJobHtml);
Check("a validThrough date in the past marks the job closed", closedResult.IsClosed);
Check("still extracts company even when the posting is closed", closedResult.Company == "Beta Inc", $"got '{closedResult.Company}'");

var plainTextClosedHtml = """<html><body><div class="banner">This job is no longer accepting applications.</div><h1>Software Engineer</h1></body></html>""";
var plainTextClosedResult = JobEnrichmentParser.ParseHtml(plainTextClosedHtml);
Check("plain-text 'no longer accepting applications' is detected with no JSON-LD present", plainTextClosedResult.IsClosed);

var noSignalHtml = "<html><body><h1>Some Job</h1><p>Great opportunity, apply today.</p></body></html>";
var noSignalResult = JobEnrichmentParser.ParseHtml(noSignalHtml);
Check("no signals found means open by default, never guessed closed", !noSignalResult.IsClosed);
Check("no signals found means no company guessed", noSignalResult.Company is null);
Console.WriteLine();

// ---- 4. RssFeedParser -----------------------------------------------------
Console.WriteLine("[RssFeedParser]");
var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-feed.rss");
var xml = File.ReadAllText(fixturePath);
var parsed = RssFeedParser.Parse(xml, "sample-feed");
Check("parses all 4 <item> entries", parsed.Count == 4, $"got {parsed.Count}");
Check("splits 'Role at Company' correctly", parsed[0].Title == ".NET Backend Developer" && parsed[0].Company == "Brightlane Technologies");
Check("splits 'Role - Company' correctly", parsed[1].Title == "Senior React Developer" && parsed[1].Company == "PixelForge Studios");
Console.WriteLine();

// ---- 5. End-to-end: parse -> normalize -> dedupe -> filter ----------------
Console.WriteLine("[End-to-end pipeline]");
var normalized = parsed.Select(JobNormalizer.Normalize).ToList();
var seenHashes = new HashSet<string>();
var deduped = new List<Job>();
foreach (var job in normalized)
{
    job.DedupeHash = DedupeService.ComputeHash(job);
    if (seenHashes.Add(job.DedupeHash))
        deduped.Add(job);
}
Check("4 raw items dedupe to 3 unique jobs", deduped.Count == 3, $"got {deduped.Count}");

var e2ePrefs = new Preferences { MustHaveKeywords = new() { ".NET", "ASP.NET" } };
var matches = deduped.Where(j => KeywordFilter.Evaluate(j, e2ePrefs).IsMatch).ToList();
Check("keyword filter narrows 3 unique jobs to 2 .NET matches", matches.Count == 2, $"got {matches.Count}");
Check("React-only job correctly excluded", matches.All(j => j.Company != "PixelForge Studios"));
Console.WriteLine();

// ---- 6. DigestBuilder -------------------------------------------------
Console.WriteLine("[DigestBuilder]");
var digest = DigestBuilder.BuildPlainText(matches, new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc));
Check("digest header includes match count", digest.Contains("2 new matches"), digest.Split('\n')[0]);
Check("digest lists both matched jobs", digest.Contains("Brightlane Technologies") && digest.Contains("Trivandrum Cloudworks"));
var emptyDigest = DigestBuilder.BuildPlainText(new List<Job>(), DateTime.UtcNow);
Check("empty digest says so instead of an empty list", emptyDigest.Contains("No new roles matched"));

var withMessage = matches.Append(new Job { Kind = JobKind.MessageAlert, Title = "New message on Naukri — Recruiter Update", Company = "Naukri", Url = "mailto:naukri.com#1", RawText = "A recruiter viewed your profile.", SourceName = "AlertMailbox" }).ToList();
var digestWithMessage = DigestBuilder.BuildPlainText(withMessage, new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc));
Check("digest puts message alerts in their own section, separate from job matches", digestWithMessage.Contains("Messages waiting for you") && digestWithMessage.Contains("New message on Naukri"));
Check("message alert does not get counted as a job match in the header", digestWithMessage.Contains("2 new matches"), digestWithMessage.Split('\n')[0]);
Console.WriteLine();

Console.WriteLine("== Result: " + (failed == 0 ? "ALL PASS" : $"{failed} FAILED") + $" ({passed} passed, {failed} failed) ==");
return failed == 0 ? 0 : 1;