namespace ApplyEngine.Domain.Entities;

public enum SourceType
{
    Rss,
    HtmlBoard,
    ImapAlert,

    /// <summary>A board whose listings only exist after client-side JS runs (React/Next.js/etc.) —
    /// a plain HTTP GET returns an empty shell. Needs a real browser (Playwright), not HtmlAgilityPack.
    /// Discovered for infoparkdaily.online/jobs/, which returns no server-rendered job markup at all.</summary>
    JsRenderedBoard,
}

/// <summary>
/// One feed, board, or alert mailbox the Collect stage pulls from.
/// Each source fetches and fails independently — one bad source never
/// blocks the others (spec §2, "every layer degrades independently").
/// </summary>
public class Source
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public SourceType Type { get; set; }
    public required string Endpoint { get; set; }
    public bool Enabled { get; set; } = true;

    public DateTime? LastRunAtUtc { get; set; }
    public string? LastStatus { get; set; }
    public int LastJobCount { get; set; }
}
