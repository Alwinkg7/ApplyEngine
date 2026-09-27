using System.Reflection;
using ApplyEngine.Domain.Processing;
using ApplyEngine.Infrastructure.Configuration;

namespace ApplyEngine.Infrastructure.Resumes;

/// <summary>
/// Source of truth for the actual resume text behind each ResumeVariantKind. Defaults to the
/// three real resume variants shipped as embedded resources in this project (Resumes/*.md) —
/// so tailoring works out of the box with no extra setup — and only reads from disk instead
/// when AppConfig.ResumeVariants explicitly points a variant at an external file, which lets
/// you edit your resume's wording later without rebuilding the app.
///
/// Loaded once and cached in memory for the process lifetime (a Phase0 run is a single
/// short-lived process anyway, so there's no staleness concern from caching an external file's
/// content for the run's duration).
/// </summary>
public class ResumeVariantStore
{
    private readonly Dictionary<ResumeVariantKind, string> _cache = new();
    private readonly ResumeVariantsConfig? _config;

    public ResumeVariantStore(ResumeVariantsConfig? config = null)
    {
        _config = config;
    }

    public string Get(ResumeVariantKind kind)
    {
        if (_cache.TryGetValue(kind, out var cached))
            return cached;

        var overridePath = kind switch
        {
            ResumeVariantKind.FullStack => _config?.FullStackPath,
            ResumeVariantKind.Frontend => _config?.FrontendPath,
            ResumeVariantKind.Backend => _config?.BackendPath,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var text = !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
            ? File.ReadAllText(overridePath)
            : ReadEmbedded(kind);

        _cache[kind] = text;
        return text;
    }

    private static string ReadEmbedded(ResumeVariantKind kind)
    {
        var fileName = kind switch
        {
            ResumeVariantKind.FullStack => "full-stack.md",
            ResumeVariantKind.Frontend => "frontend.md",
            ResumeVariantKind.Backend => "backend.md",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"ApplyEngine.Infrastructure.Resumes.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resume resource '{resourceName}' not found — check the EmbeddedResource glob in ApplyEngine.Infrastructure.csproj still matches Resumes/*.md.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}