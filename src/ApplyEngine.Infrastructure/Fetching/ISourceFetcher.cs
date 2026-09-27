using ApplyEngine.Domain.Entities;

namespace ApplyEngine.Infrastructure.Fetching;

/// <summary>
/// One implementation per source type (spec §2 "Services" / §3 Layer 1 "one
/// small fetcher module per source"). A fetcher throwing never takes down the
/// run — Phase0Runner catches per-source and keeps going.
/// </summary>
public interface ISourceFetcher
{
    SourceType HandlesType { get; }
    Task<List<Job>> FetchAsync(Source source, CancellationToken ct = default);
}
