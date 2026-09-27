# ApplyEngine — Phase 0

Console app + scheduled task: fetch a couple of sources, dedupe, hard-filter,
email yourself a digest. No UI, no LLM, no apply. This is the weekend build
from the spec (`§5 Build phases`) — it alone stops the manual daily search.

## Project layout

```
ApplyEngine.sln
src/
  ApplyEngine.Domain/          entities + normalize/dedupe/filter/RSS-parse/digest — zero NuGet packages
  ApplyEngine.Infrastructure/  EF Core (SQLite), HtmlAgilityPack board fetcher, MailKit IMAP + SMTP
  ApplyEngine.Phase0/          the console runner — wires Domain + Infrastructure together
tools/
  ApplyEngine.SelfCheck/       zero-package runnable proof of the Domain pipeline logic
fixtures/
  sample-feed.rss              used by ApplyEngine.SelfCheck
```

`ApplyEngine.Domain` has no dependencies on purpose — every algorithm that
matters (dedupe hashing, keyword filtering, RSS parsing, digest formatting)
lives there and is provable without a network connection or a package
restore. `ApplyEngine.Infrastructure` is the IO shell around it: swap SQLite
for SQL Server, or add a new board adapter, without touching the logic in
Domain.

## A note on where this was built

This was scaffolded and verified in a sandboxed cloud environment whose
network policy blocks nuget.org entirely (confirmed — even `dotnet restore`
on an empty project fails). Because of that:

- **`ApplyEngine.Domain` and `ApplyEngine.SelfCheck` were actually built and
  run** in that sandbox — 20/20 checks pass. This proves the parsing,
  normalization, dedupe, filtering, and digest logic is correct, not just
  that it compiles.
- **`ApplyEngine.Infrastructure` and `ApplyEngine.Phase0`** reference real
  packages (EF Core Sqlite, HtmlAgilityPack, MailKit) and were written and
  reviewed carefully, but could not be restored/built in that sandbox. Run
  `dotnet build` on your own machine (normal internet access, no proxy
  blocking nuget.org) to compile and exercise these for the first time —
  that should be close to a non-event, but treat the first build as an
  actual verification step, not a formality.

## Running it

```bash
cd src/ApplyEngine.Phase0
cp appsettings.Example.json appsettings.json
# edit appsettings.json: your sources, your preferences, your credentials

dotnet run --project . -- appsettings.json
```

With `"DryRun": true` (the default) it needs no email credentials at all —
it writes `digest-output.txt` next to the executable instead of sending
anything. Get the RSS source working end to end with `DryRun: true` first,
then add SMTP credentials and flip it to `false`.

Re-run it daily (cron, Windows Task Scheduler, or just by hand for now —
Phase 1 moves this into a proper Hangfire schedule). Each run only stores and
digests jobs it hasn't seen before; the SQLite `Jobs` table is the memory.

## Credentials — read this before editing appsettings.json

`Imap`/`Smtp` need a Google **App Password**, not your real Gmail password:
Google Account -> Security -> 2-Step Verification -> App passwords. Use a
mailbox dedicated to this app if you can, rather than your primary inbox.
`appsettings.json` (the real one, not `.Example.json`) is already gitignored
— keep it that way, and never paste a live credential into a chat, issue, or
commit message. If a real password ever ends up somewhere it shouldn't
(a chat log, a shared doc, a public repo), treat it as compromised and
rotate it immediately, regardless of whether you think anyone saw it.

## Wiring up a real HTML board

`HtmlBoards:TechnoparkBoard` and `HtmlBoards:InfoparkBoard` in
`appsettings.Example.json` are **placeholders** — this sandbox had no
outbound access to fetch the real Technopark/Infopark markup, so both the
`Endpoint` URLs and the XPaths are illustrative only, not verified. Infopark
in particular may not have one central listings page the way Technopark's
`/job-search` does — check whether jobs are listed centrally or only
per-company before assuming the XPath approach even applies; if it's
per-company, you likely want a small RSS/API check per company instead of
one HtmlBoard source. To wire up a real board:

1. Open the board in a browser, right-click one job listing, "Inspect".
2. Find the repeating container element (the `<div>`/`<li>`/`<article>` that
   wraps one listing) — that's your `ItemXPath`, relative to the document root.
3. Within that container, find the title, company, location, and link
   elements — those XPaths are relative to the item, hence the leading `.`
   (e.g. `.//h3`).
4. Set `Enabled: true` on that source and run with `DryRun: true` to confirm
   it extracts sane-looking jobs before you trust it unattended.

If a board's markup doesn't cooperate with XPath (heavy client-side
rendering, no server HTML — check by opening the URL and viewing source; if
the job listings aren't in the raw HTML, `HtmlBoardFetcher` can't see them
either), it's a `JsRenderedBoard` source instead — see the next section.

## Wiring up a JS-rendered board (Playwright)

Some boards — `infoparkdaily.online/jobs/` is a confirmed example, a plain
HTTP fetch returns an empty page shell with no job markup at all — only
render their listings after client-side JS runs. `HtmlBoards` (HttpClient +
HtmlAgilityPack) cannot see anything on a page like that; `JsRenderedBoards`
(Playwright, a real headless Chromium) can, because it actually executes the
page's JavaScript before scraping.

This is the same tool the spec scopes for Phase 3's portal auto-submit
engine, used here first in its simpler read-only role — a good place to get
comfortable with Playwright's waiting/selector model before Phase 3 asks it
to fill out forms.

Setup, once you're on a machine with normal internet access:

```bash
cd src/ApplyEngine.Phase0
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium   # downloads a headless browser (~150 MB)
# no pwsh? use: dotnet tool install --global Microsoft.Playwright.CLI
#              playwright install chromium
```

Then, same as an HtmlBoard: open the real page, inspect one job card, and
fill in `JsRenderedBoards:InfoparkDailyJobs` in `appsettings.json`:

- `ItemSelector` — CSS selector for the repeating job-card container.
- `TitleSelector` / `CompanySelector` / `LocationSelector` — CSS selectors
  *relative to* one card.
- `LinkSelector` — the `<a>` whose `href` is the job link.
- `WaitForSelector` — set this to the same value as `ItemSelector`. It's
  what makes the fetcher wait for the client-side render to actually finish
  instead of scraping an empty page and silently returning zero jobs.

The placeholder selectors in `appsettings.Example.json`
(`[class*=job-card]`, `h2, h3`, etc.) are educated guesses only — this
sandbox could reach the page well enough to learn it's React/Next.js-style
and has no RSS feed, but couldn't execute its JavaScript to see the real
class names. Expect to spend a few minutes in DevTools here before it works.

## Wiring up alert-email ingestion (IMAP)

1. Set a LinkedIn / Naukri / Indeed job alert for your exact query, delivered
   to a mailbox you control.
2. Fill in the `Imap` section with an **app-specific password**, never your
   real account password (Gmail/Outlook both support generating one).
3. Enable the `AlertMailbox` source, `DryRun: true`, run once, check
   `digest-output.txt`. `ImapAlertFetcher` extracts job-view links from the
   alert email and marks it read (and optionally moves it to a "Processed"
   folder) so the next run doesn't reprocess it.

## Moving to SQL Server later

`AppDbContext` (in `ApplyEngine.Infrastructure/Data/`) is plain EF Core —
nothing about the entities is SQLite-specific. To move to the production
target from the spec: add `Microsoft.EntityFrameworkCore.SqlServer`, change
`UseSqlite(...)` to `UseSqlServer(...)` in `Program.cs`, update the
connection string, and run `dotnet ef migrations add Initial` /
`dotnet ef database update`. Nothing in `ApplyEngine.Domain` changes.

## What's deliberately NOT here yet

No dashboard, no LLM fit-scoring, no resume tailoring, no email drafting, no
sending an actual application anywhere. That's Phases 1–3 in the spec —
building them on top of this foundation, not a rewrite.
