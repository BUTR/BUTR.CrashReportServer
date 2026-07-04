namespace BUTR.CrashReport.Server.Options;

public record CompressionOptions
{
    public int Level { get; set; } = 19;

    /// <summary>
    /// How long the active-dictionary pointer per (tenant, kind, version-group) is cached before re-checking the
    /// database. Only the pointer is short-lived; the dictionary bytes themselves are immutable and cached forever.
    /// </summary>
    public int ActiveDictionaryCacheSeconds { get; set; } = 60;
}