using System.Text.Json;
using ApplyEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ApplyEngine.Infrastructure.Data;

/// <summary>
/// Phase 0's slice of the full data model (spec §4) was just Jobs and
/// Sources. Phase 1 added Preferences (moved out of appsettings.json so the
/// dashboard can edit it live). Phase 2 adds Matches (spec §4) — one row per
/// (Job, Preferences.Version) that cleared the free hard filter, scored by
/// LlmFitScorer. ResumeVersions, EmailDrafts, Applications, SendLog, and
/// SubmissionAttempts still arrive later in Phase 2/3 once there's something
/// to log against them.
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Preferences> Preferences => Set<Preferences>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<ResumeVariantRecord> ResumeVariantRecords => Set<ResumeVariantRecord>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => j.DedupeHash).IsUnique();
            e.Property(j => j.Title).IsRequired();
            e.Property(j => j.Company).IsRequired();
            e.Property(j => j.Url).IsRequired();
        });

        modelBuilder.Entity<Source>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<Preferences>(e =>
        {
            // Singleton row — Id is always 1 (see PreferencesStore), never
            // IDENTITY-generated, so re-seeding always lands on the same row.
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();

            // SQL Server has no native list/array column type, so each
            // List<string> is round-tripped through JSON as an NVARCHAR(MAX).
            // The ValueComparer is required for change-tracking: without it,
            // EF can't tell an in-place-edited list apart from the one it
            // loaded, and silently skips saving the edit.
            var stringListComparer = new ValueComparer<List<string>>(
                (a, b) => (a ?? new()).SequenceEqual(b ?? new()),
                v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s)),
                v => v.ToList());

            foreach (var listProperty in new[]
                     {
                         nameof(Domain.Entities.Preferences.MustHaveKeywords),
                         nameof(Domain.Entities.Preferences.NiceToHaveKeywords),
                         nameof(Domain.Entities.Preferences.Locations),
                         nameof(Domain.Entities.Preferences.WorkModes),
                         nameof(Domain.Entities.Preferences.ExcludeCompanies),
                     })
            {
                e.Property<List<string>>(listProperty)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new(),
                        stringListComparer);
            }
        });

        modelBuilder.Entity<Match>(e =>
        {
            e.HasKey(m => m.Id);

            // One score per (job, preferences version) — re-running the pipeline
            // against unchanged preferences should never produce duplicate Match
            // rows for the same job (see JobPipelineService's TryGetExisting check).
            e.HasIndex(m => new { m.JobId, m.PreferencesVersion }).IsUnique();

            e.Property(m => m.Status)
                .HasConversion<string>(); // readable directly in SQL when debugging, unlike a bare int
        });

        modelBuilder.Entity<ResumeVariantRecord>(e =>
        {
            // Kind is the enum's string name ("FullStack", not 0) — same reasoning as
            // Match.Status above — and there's exactly one row per ResumeVariantKind, so it
            // doubles as the natural primary key rather than a separate generated Id.
            e.HasKey(r => r.Kind);
        });
    }
}