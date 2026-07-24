namespace Crate.Api;

// Plain POCOs for Dapper (snake_case -> PascalCase enabled in Db).

public sealed class Source
{
    public long Id { get; set; }
    public string Kind { get; set; } = "youtube";
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string DestDir { get; set; } = "";
    public string? Cond { get; set; }
    public string? Pref { get; set; }
    public string? MinFormat { get; set; }
    public bool UpgradeLowerQuality { get; set; }
    public string? ScheduleCron { get; set; }
    public string? Profile { get; set; }
    public bool Enabled { get; set; } = true;
    public string? LastRunAt { get; set; }
}

public sealed class Track
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public string? ExternalId { get; set; }
    public string? RawTitle { get; set; }
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
    public int? DurationSec { get; set; }
    public string? MbRecordingId { get; set; }
    public string? Isrc { get; set; }
    public int? ExpectedLenSec { get; set; }
    public string State { get; set; } = "Pending";
    public bool Enriched { get; set; }
    public string? FilePath { get; set; }
    public string? UpdatedAt { get; set; }
}

public record TrackEdit(string? Artist, string? Title, string? Album);
public record MatchInput(string Path);

public sealed class LibraryFile
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public int? DurationSec { get; set; }
    public string? Fingerprint { get; set; }
    public long? Mtime { get; set; }
    public long? Size { get; set; }
    public long? MatchedTrackId { get; set; }
    public string? ScannedAt { get; set; }
}
