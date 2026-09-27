namespace ApplyEngine.Domain.Entities;

/// <summary>
/// The actual resume text behind one ResumeVariantKind (FullStack/Frontend/Backend), moved into
/// the database so the dashboard can let you paste/edit your real, ATS-formatted resume directly
/// instead of hand-editing the embedded/external .md files ResumeVariantStore originally read
/// (see its remarks) — no rebuild or redeploy needed to change your resume's wording.
///
/// One row per variant, keyed by Kind (the enum's string name, e.g. "FullStack" — same
/// "readable directly in SQL" reasoning as Match.Status's string conversion). A row only exists
/// once you've actually saved an edit through PUT /api/resumes/{kind} — see
/// ResumeVariantRepository's remarks for why this is deliberately NOT auto-seeded from the
/// shipped defaults just by viewing them.
/// </summary>
public class ResumeVariantRecord
{
    public required string Kind { get; set; }

    public required string MarkdownContent { get; set; }

    /// <summary>When this variant was last saved through the dashboard. Null is never expected
    /// in practice (a row is only ever created alongside setting this), but left nullable rather
    /// than defaulted to avoid implying a save happened when it didn't.</summary>
    public DateTime? UpdatedAtUtc { get; set; }
}