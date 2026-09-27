using ApplyEngine.Domain.Entities;
using ApplyEngine.Domain.Processing;
using ApplyEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ApplyEngine.Infrastructure.Resumes;

/// <summary>One variant's current text for the dashboard's editor. IsCustomized is false when
/// this is still the shipped/embedded default (no row saved yet) — the dashboard uses this to
/// show a "using default" vs. "your saved resume" badge.</summary>
public record ResumeVariantDto(ResumeVariantKind Kind, string Markdown, DateTime? UpdatedAtUtc, bool IsCustomized);

/// <summary>
/// DB-backed override for resume variant text, sitting on top of the original file/embedded
/// ResumeVariantStore. Once you save an edit (via PUT /api/resumes/{kind} — the dashboard's
/// "paste your own resume" editor), the saved row always wins over the shipped default;
/// GetTextAsync only ever falls back to ResumeVariantStore when no row exists yet for that Kind.
///
/// This means ApplyEngine keeps working with zero setup (the three example resumes embedded in
/// ApplyEngine.Infrastructure), but you're never stuck hand-editing a .md file and rebuilding to
/// change your real resume's wording — paste it into the dashboard once per variant and every
/// pipeline run from then on tailors from your own text.
///
/// Deliberately NOT auto-seeding the DB from the defaults just because GetAllAsync was called to
/// render the editor — a row here means "you actually saved something", which is useful signal
/// on its own (IsCustomized) and avoids silently freezing a copy of the embedded default at
/// whatever moment someone happened to open the Resumes tab.
/// </summary>
public static class ResumeVariantRepository
{
    public static async Task<string> GetTextAsync(
        AppDbContext db, ResumeVariantKind kind, ResumeVariantStore fallbackStore, CancellationToken ct = default)
    {
        var record = await db.ResumeVariantRecords.FirstOrDefaultAsync(r => r.Kind == kind.ToString(), ct);
        return record?.MarkdownContent ?? fallbackStore.Get(kind);
    }

    public static async Task<List<ResumeVariantDto>> GetAllAsync(
        AppDbContext db, ResumeVariantStore fallbackStore, CancellationToken ct = default)
    {
        var existing = await db.ResumeVariantRecords.ToDictionaryAsync(r => r.Kind, ct);
        var result = new List<ResumeVariantDto>();

        foreach (var kind in Enum.GetValues<ResumeVariantKind>())
        {
            result.Add(existing.TryGetValue(kind.ToString(), out var record)
                ? new ResumeVariantDto(kind, record.MarkdownContent, record.UpdatedAtUtc, IsCustomized: true)
                : new ResumeVariantDto(kind, fallbackStore.Get(kind), UpdatedAtUtc: null, IsCustomized: false));
        }

        return result;
    }

    public static async Task<ResumeVariantDto> UpsertAsync(
        AppDbContext db, ResumeVariantKind kind, string markdown, CancellationToken ct = default)
    {
        var record = await db.ResumeVariantRecords.FirstOrDefaultAsync(r => r.Kind == kind.ToString(), ct);
        var now = DateTime.UtcNow;

        if (record is null)
        {
            record = new ResumeVariantRecord { Kind = kind.ToString(), MarkdownContent = markdown, UpdatedAtUtc = now };
            db.ResumeVariantRecords.Add(record);
        }
        else
        {
            record.MarkdownContent = markdown;
            record.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return new ResumeVariantDto(kind, record.MarkdownContent, record.UpdatedAtUtc, IsCustomized: true);
    }
}