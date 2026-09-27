using ApplyEngine.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ApplyEngine.Infrastructure.Data;

/// <summary>
/// Phase 1: Preferences moves from appsettings.json into the database, so the
/// dashboard can edit it without a rebuild/redeploy. There's exactly one row
/// (Id = 1) — this is a singleton, not a multi-profile system.
///
/// On an empty table (a brand-new database, or the first run right after
/// upgrading to Phase 1) it seeds itself once from whatever Preferences block
/// is still sitting in appsettings.json. After that first seed, the database
/// row is the only thing that matters — the appsettings.json Preferences
/// block is never read again, so editing it has no effect once a run has
/// happened. Edit preferences through the dashboard (or directly in the
/// Preferences table) from then on.
/// </summary>
public static class PreferencesStore
{
    public const int SingletonId = 1;

    public static async Task<Preferences> GetOrSeedAsync(
        AppDbContext db, Preferences seedFromConfigIfEmpty, CancellationToken ct = default)
    {
        var existing = await db.Preferences.FirstOrDefaultAsync(p => p.Id == SingletonId, ct);
        if (existing is not null) return existing;

        seedFromConfigIfEmpty.Id = SingletonId;
        db.Preferences.Add(seedFromConfigIfEmpty);
        await db.SaveChangesAsync(ct);
        return seedFromConfigIfEmpty;
    }
}